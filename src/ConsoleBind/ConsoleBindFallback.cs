using System;
using System.Collections.Generic;
using System.Text;
using CommandSystem;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using MEC;
using RemoteAdmin;
using UnityEngine;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace ServerKeybinds;

/// <summary>
/// TEMPORARY (15.0 beta): a console route to the registry's keybinds. The 15.0 beta client receives the
/// settings pack but has no Server-Specific Settings tab, so its players can never assign a keybind. Players
/// bind a key with the native console instead (<c>cmdbind &lt;key&gt; .kb &lt;name&gt;</c>), and each press
/// arrives here as one <c>.kb</c> command that is fed through the registry's normal latch and dispatch.
/// Remove this folder, its two hooks in <see cref="KeybindRegistry"/>, the config key and the README section
/// once the 15.0 client ships the tab.
///
/// The native console binding fires once on key DOWN and never reports the release, so a hold cannot be
/// reproduced. Each <c>.kb</c> is therefore either a tap (press now, release <see cref="TapReleaseSeconds"/>
/// later) or, for hold-type keys, a toggle (press now, release on the next <c>.kb</c> for that key or after
/// <see cref="ToggleLimitSeconds"/>). The registry's own releases (role change, death, rebuild) still apply.
/// </summary>
internal static class ConsoleBindFallback
{
    internal const float TapReleaseSeconds = 0.15f;
    internal const float ToggleLimitSeconds = 30f;
    private const float HintDelaySeconds = 20f;
    private const float LobbyRetrySeconds = 5f;
    private const double RoundSettleSeconds = 12d;

    // Per (UserId, setting id): the token of the press whose scheduled release is still owed.
    private static readonly Dictionary<(string UserId, int SettingId), int> PendingRelease = new();
    private static readonly HashSet<string> HintedUsers = new(StringComparer.Ordinal);
    private static bool _enabled;
    private static bool _commandsRegistered;
    private static int _token;
    private static int _generation;

    internal static bool Enabled => _enabled;

    internal static void Start()
    {
        _enabled = RegistrySettings.LoadConsoleBindFallback();
        if (!_enabled)
        {
            Logger.Info("[ServerKeybinds] Console-bind fallback is off (console_bind_fallback: false).");
            return;
        }

        _generation++;
        PlayerEvents.Joined += OnJoined;
        if (!_commandsRegistered)
        {
            // Registered once for the process, like the RA command; the commands answer "off" when disabled.
            bool kb = CommandLoader.TryRegisterCommand(new KbCommand(), QueryProcessor.DotCommandHandler, "ServerKeybinds");
            bool binds = CommandLoader.TryRegisterCommand(new BindsCommand(), QueryProcessor.DotCommandHandler, "ServerKeybinds");
            _commandsRegistered = kb && binds;
            if (!_commandsRegistered)
            {
                Logger.Warn("[ServerKeybinds] Player console commands '.kb'/'.binds' could not be registered (name already taken?).");
            }
        }

        Logger.Info("[ServerKeybinds] Console-bind fallback is on: player console commands '.kb' and '.binds' (temporary, 15.0 beta).");
    }

    internal static void Stop()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        _generation++;
        PlayerEvents.Joined -= OnJoined;
        PendingRelease.Clear();
        ConsoleBindHint.Dispose();
    }

    /// <summary>
    /// A hold-type key wants a toggle. Registered keys say so in their own label or hint ("（按住）",
    /// "按住操作，松开停止", "(Hold)"); a hint that only mentions holding ANOTHER key does not start with it.
    /// </summary>
    internal static bool IsHoldKey(KeybindBlock.Binding binding) =>
        binding.Label.Contains("按住") ||
        binding.Label.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
        binding.Hint.StartsWith("按住", StringComparison.Ordinal);

    /// <summary>The name <c>.kb</c> accepts: a short one for the shared ability keys, else the setting id.</summary>
    internal static string ShortName(KeybindRegistry.ConsoleKeybind keybind)
    {
        if (keybind.SettingId == AbilityKeys.PrimaryUseId) return "p";
        if (keybind.SettingId == AbilityKeys.SecondaryUseId) return "s";
        if (keybind.SettingId == AbilityKeys.ModifierId) return "m";
        int slot = keybind.SettingId - AbilityKeys.FirstSlotId;
        if (slot >= 0 && slot < AbilityKeys.SlotCount) return (slot + 1).ToString();
        return keybind.SettingId.ToString();
    }

    internal static bool TryResolve(Player player, string name, out KeybindRegistry.ConsoleKeybind keybind)
    {
        foreach (KeybindRegistry.ConsoleKeybind candidate in KeybindRegistry.ConsoleKeybindsFor(player))
        {
            if (string.Equals(ShortName(candidate), name, StringComparison.OrdinalIgnoreCase) ||
                candidate.SettingId.ToString() == name)
            {
                keybind = candidate;
                return true;
            }
        }

        keybind = default;
        return false;
    }

    /// <summary>One console press. Returns the short readout for the player's console.</summary>
    internal static bool Press(Player player, KeybindRegistry.ConsoleKeybind keybind, bool toggle, out string response)
    {
        KeybindBlock.Binding binding = keybind.Binding;
        if (!player.IsAlive && !binding.AllowSpectatorTrigger)
        {
            // The settings-tab key is ignored while spectating too.
            response = $"{binding.Label}：观战时不可用。";
            return false;
        }

        var key = (player.UserId, keybind.SettingId);
        bool latched = KeybindRegistry.IsLatched(player, keybind.SettingId);
        if (toggle && latched)
        {
            PendingRelease.Remove(key);
            KeybindRegistry.InjectKeyEdge(player, keybind.SettingId, isPressed: false);
            response = $"{binding.Label}：松开";
            return true;
        }

        if (latched)
        {
            // A tap while still held from the previous tap: finish that press first so this one is a new edge.
            PendingRelease.Remove(key);
            KeybindRegistry.InjectKeyEdge(player, keybind.SettingId, isPressed: false);
        }

        if (!KeybindRegistry.InjectKeyEdge(player, keybind.SettingId, isPressed: true))
        {
            response = $"{binding.Label}：当前不可用。";
            return false;
        }

        int token = ++_token;
        PendingRelease[key] = token;
        int generation = _generation;
        Timing.CallDelayed(toggle ? ToggleLimitSeconds : TapReleaseSeconds,
            () => ReleaseIfOwed(key, token, generation));
        response = toggle ? $"{binding.Label}：按住（再按一次松开）" : binding.Label;
        return true;
    }

    private static void ReleaseIfOwed((string UserId, int SettingId) key, int token, int generation)
    {
        if (generation != _generation || !PendingRelease.TryGetValue(key, out int owed) || owed != token)
        {
            return;
        }

        PendingRelease.Remove(key);
        Player? player = Player.Get(key.UserId);
        // No latch means the registry already released it (role change, death, rebuild): nothing is owed.
        if (player != null && KeybindRegistry.IsLatched(player, key.SettingId))
        {
            KeybindRegistry.InjectKeyEdge(player, key.SettingId, isPressed: false);
        }
    }

    /// <summary>The <c>.binds</c> listing: every key visible to the player now, with its native bind line.</summary>
    internal static string Listing(Player player)
    {
        List<KeybindRegistry.ConsoleKeybind> keybinds = KeybindRegistry.ConsoleKeybindsFor(player);
        if (keybinds.Count == 0)
        {
            return "你当前没有可绑定的按键。";
        }

        var text = new StringBuilder();
        text.AppendLine("你的客户端没有「服务器专属设置」页时，用控制台绑定按键：");
        text.AppendLine("逐行输入下面的命令（按键可以换成你想要的任意键），之后按该键即可。绑定会一直保存在本机。");
        string? group = null;
        foreach (KeybindRegistry.ConsoleKeybind keybind in keybinds)
        {
            string blockName = BlockName(keybind.Block);
            if (blockName != group)
            {
                group = blockName;
                text.AppendLine();
                text.AppendLine($"【{blockName}】");
            }

            string keyName = keybind.Binding.DefaultKey == KeyCode.None ? "<按键>" : keybind.Binding.DefaultKey.ToString();
            string mode = IsHoldKey(keybind.Binding) ? "（按一次按住，再按一次松开）" : string.Empty;
            text.AppendLine($"cmdbind {keyName} .kb {ShortName(keybind)}    {keybind.Binding.Label}{mode}");
        }

        text.AppendLine();
        text.Append("也可以直接输入 .kb <名称> 按一次；.kb <名称> toggle 按住/松开切换，.kb <名称> tap 点按。");
        return text.ToString();
    }

    private static string BlockName(KeybindBlock block)
    {
        foreach (KeybindBlock.HeaderEntry header in block.Headers)
        {
            if (header.Local == 0) return header.Name;
        }

        return block.Headers.Count > 0 ? block.Headers[0].Name : block.Owner;
    }

    private static void OnJoined(PlayerJoinedEventArgs ev)
    {
        Player? player = ev.Player;
        if (player == null || player.IsHost || player.IsDummy || string.IsNullOrWhiteSpace(player.UserId))
        {
            return;
        }

        string userId = player.UserId;
        int generation = _generation;
        Timing.CallDelayed(HintDelaySeconds, () => CheckSettingsTab(userId, generation));
    }

    /// <summary>
    /// A client with the tab reports its settings version right after the join send; version 0 this long
    /// after joining means it never will, so point the player at <c>.binds</c> once per server session,
    /// after the round has started.
    /// </summary>
    private static void CheckSettingsTab(string userId, int generation)
    {
        if (generation != _generation || HintedUsers.Contains(userId))
        {
            return;
        }

        Player? player = Player.Get(userId);
        if (player?.ReferenceHub != null && (!Round.IsRoundStarted || Round.Duration.TotalSeconds < RoundSettleSeconds))
        {
            // The lobby's own controls and the native role intro both sit where the notice goes; ask once the
            // round is under way and the intro has faded.
            Timing.CallDelayed(LobbyRetrySeconds, () => CheckSettingsTab(userId, generation));
            return;
        }

        if (player?.ReferenceHub == null || !player.IsReady ||
            ServerSpecificSettingsSync.GetUserVersion(player.ReferenceHub) != 0 ||
            KeybindRegistry.ConsoleKeybindsFor(player).Count == 0)
        {
            return;
        }

        HintedUsers.Add(userId);
        player.SendConsoleMessage(
            "本服的技能按键需要绑定后才能使用。你的客户端没有「服务器专属设置」页，请输入 .binds 查看控制台绑定命令。",
            "yellow");

        string shown;
        try
        {
            shown = ConsoleBindHint.TryShow(player) ? "hint shown" : "hint unavailable";
        }
        catch (Exception exception)
        {
            // HsmAdapter missing at runtime (or built without it): the console line above still went out.
            shown = $"hint unavailable ({exception.GetType().Name})";
        }

        Logger.Info($"[ServerKeybinds] {player.Nickname} ({player.PlayerId}) never reported a settings tab " +
            $"(version 0 after {HintDelaySeconds:0} s); pointed them at .binds: {shown}.");
    }

    private static bool TryGetPlayer(ICommandSender sender, out Player player, out string response)
    {
        player = Player.Get(sender)!;
        if (player == null || sender is not PlayerCommandSender)
        {
            response = "只有玩家可以使用这个命令。";
            return false;
        }

        if (!_enabled)
        {
            response = "本服未开启控制台按键。";
            return false;
        }

        response = string.Empty;
        return true;
    }

    /// <summary><c>.kb &lt;name&gt; [tap|toggle]</c>: one press of a registered keybind.</summary>
    private sealed class KbCommand : ICommand
    {
        public string Command => "kb";

        public string[] Aliases => [];

        public string Description => "临时：按一次服务器按键（用 cmdbind 绑定到按键）。输入 .binds 查看列表。";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!TryGetPlayer(sender, out Player player, out response))
            {
                return false;
            }

            if (arguments.Count == 0)
            {
                response = "用法：.kb <名称> [tap|toggle]。输入 .binds 查看名称和绑定命令。";
                return false;
            }

            if (!TryResolve(player, arguments.At(0).Trim(), out KeybindRegistry.ConsoleKeybind keybind))
            {
                response = $"没有名为 {arguments.At(0)} 的按键，或你当前的角色用不到它。输入 .binds 查看列表。";
                return false;
            }

            bool toggle = IsHoldKey(keybind.Binding);
            if (arguments.Count > 1)
            {
                switch (arguments.At(1).Trim().ToLowerInvariant())
                {
                    case "toggle":
                    case "hold":
                        toggle = true;
                        break;
                    case "tap":
                        toggle = false;
                        break;
                    default:
                        response = "用法：.kb <名称> [tap|toggle]";
                        return false;
                }
            }

            return Press(player, keybind, toggle, out response);
        }
    }

    /// <summary><c>.binds</c>: the player's keybinds with the exact native bind line for each.</summary>
    private sealed class BindsCommand : ICommand
    {
        public string Command => "binds";

        public string[] Aliases => [];

        public string Description => "临时：列出你当前可用的服务器按键和控制台绑定命令。";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!TryGetPlayer(sender, out Player player, out response))
            {
                return false;
            }

            response = Listing(player);
            return true;
        }
    }
}
