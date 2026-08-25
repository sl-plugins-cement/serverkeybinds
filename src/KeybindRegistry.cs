using System;
using System.Collections.Generic;
using System.Linq;
using CentralAuth;
using LabApi.Features.Wrappers;
using MEC;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace ServerKeybinds;

/// <summary>
/// The single, process-wide registry for SCP:SL Server-Specific Settings, shared by every
/// plugin on the server. It exists because <see cref="ServerSpecificSettingsSync.DefinedSettings"/> is one
/// static array shared by ALL plugins: registering settings independently (as plugins used to) means rival
/// merge paths that drop or duplicate each other's entries and risk id collisions. This type owns ONE merge
/// path, ONE pair of event subscriptions, and a fixed <see cref="SssIdBlocks"/> allocation so ids never collide.
///
/// Usage from any plugin:
/// <code>
/// var block = KeybindRegistry.ClaimBlock(SssIdBlocks.Reinforcements, "Task Force")
///     .Header("Task Force")
///     .Add(1, "Deploy SRA", KeyCode.X, "Press while equipped to deploy.", OnDeploy);
/// block.Enable();  // in Plugin.Enable
/// // block.Disable();  // in Plugin.Disable
/// </code>
///
/// API 3 owns keybinds, dropdowns, sliders, and two-button toggles, and it also owns the ORDER of the
/// menu: blocks are grouped by <see cref="SettingsCategory"/> under one synthesised group header each, so
/// what the player sees no longer depends on plugin load order. This ships as a dependency LIBRARY (deployed to LabAPI's <c>dependencies/global</c>), so it loads exactly
/// once before any plugin and its statics are shared. A plugin that hard-references it but is missing the DLL
/// fails to load entirely rather than running half-broken.
/// </summary>
public static class KeybindRegistry
{
    /// <summary>Bumped on any breaking change to this API; consumers can assert it in their Enable.</summary>
    /// <summary>
    /// NOT a const, so it is read from whichever assembly is loaded rather than baked into the caller.
    ///
    /// DO NOT USE THIS TO DETECT AN OLD REGISTRY. API 2 declared <c>ApiVersion</c> as a const FIELD; a
    /// consumer compiled against API 3 emits a call to the property GETTER, which does not exist there, so
    /// reading it against an API 2 DLL throws <c>MissingMethodException</c> exactly like calling
    /// <c>AddTwoButtons</c> would. It is no safer than the members it would be guarding.
    ///
    /// The assembly version is not a boundary either: this assembly is not strong-named, so the CLR
    /// ignores version when binding it (see the csproj).
    ///
    /// The ONLY safe compatibility check is to probe for the members themselves by reflection -
    /// <c>typeof(KeybindBlock).GetMethod("AddTwoButtons")</c> and friends - and to make the API 3 calls
    /// from a separate <c>[MethodImpl(MethodImplOptions.NoInlining)]</c> method, because the JIT resolves
    /// call targets when it compiles a method, not when the call executes. Both consumers in this metarepo
    /// do that; copy them rather than this property.
    ///
    /// What this IS good for: logging and diagnostics from code that already knows API 3 is present, and
    /// as a plain version guard between two future releases that both expose it as a property.
    /// </summary>
    public static int ApiVersion => 3;

    /// <summary>
    /// Language for the category headers this registry synthesises. An empty value or <c>cn</c> renders
    /// Chinese, <c>en</c> renders English. Everything else in the menu is authored by the consuming plugin,
    /// which localises its own strings; only these headers belong to the registry.
    ///
    /// <c>DefinedSettings</c> is one global array, so this cannot be per-player. It follows the metarepo
    /// default of falling back to Chinese. A consumer may set it from its own language config; last writer
    /// wins, so a server should not set it from more than one plugin.
    /// </summary>
    public static string Language { get; set; } = string.Empty;

    private static readonly Dictionary<int, KeybindBlock> Blocks = new();
    private static readonly Dictionary<int, ActiveBinding> ActiveBindings = new();
    private static readonly Dictionary<int, ActiveValueSetting> ActiveValueSettings = new();
    private static readonly Dictionary<ReferenceHub, HashSet<int>> Pressed = new();
    private static readonly HashSet<int> WarnedForeignIds = new();
    private static bool _subscribed;
    private static Predicate<ReferenceHub>? _previousJoinFilter;

    /// <summary>
    /// Claims the 1000-wide id block based at <paramref name="baseId"/> (use a <see cref="SssIdBlocks"/> constant).
    /// Throws if the base is not 1000-aligned or the block is already claimed — turning silent runtime id
    /// collisions into a hard load-time failure.
    /// </summary>
    public static KeybindBlock ClaimBlock(int baseId, string ownerName)
    {
        if (baseId % SssIdBlocks.BlockWidth != 0)
        {
            throw new ArgumentException($"Block base {baseId} must be a multiple of {SssIdBlocks.BlockWidth}.", nameof(baseId));
        }

        // The registry synthesises its category headers inside this block, at RegistryHeaders +
        // (int)category - and Gameplay is 0, so a consumer claiming this base and adding the conventional
        // local-0 header would emit a SECOND entry with id 23000. Blocks are 1000-aligned, so this base is
        // the only claimable one that can overlap the reserved range.
        if (baseId == SssIdBlocks.RegistryHeaders)
        {
            throw new ArgumentException(
                $"Block base {baseId} is reserved for the registry's own category headers. " +
                "Pick another base in SssIdBlocks.",
                nameof(baseId));
        }

        // The actual claim (and the collision check) happens at Enable, so that a plugin reload — which
        // Disables (releasing the base) then Enables again — can re-claim its own block without throwing.
        // Blocks are fixed-width and 1000-aligned, so two blocks can only collide if they share a base.
        return new KeybindBlock(baseId, ownerName);
    }

    /// <summary>Every active keybind across all plugins, for diagnostics (e.g. a "list keybinds" admin command).</summary>
    public static IReadOnlyList<KeybindInfo> Registered
    {
        get
        {
            List<KeybindInfo> list = new();
            foreach (KeybindBlock block in Blocks.Values)
            {
                if (!block.Active)
                {
                    continue;
                }

                foreach (KeybindBlock.Binding binding in block.Bindings.Values)
                {
                    list.Add(new KeybindInfo(block.BaseId + binding.Local, binding.Label, binding.DefaultKey, block.Owner));
                }
            }

            return list;
        }
    }

    internal static void EnableBlock(KeybindBlock block)
    {
        if (Blocks.TryGetValue(block.BaseId, out KeybindBlock existing) && !ReferenceEquals(existing, block))
        {
            throw new InvalidOperationException(
                $"SSS block {block.BaseId} is already claimed by '{existing.Owner}' (requested by '{block.Owner}').");
        }

        Blocks[block.BaseId] = block;
        block.Active = true;
        EnsureSubscribed();
        Rebuild();
        LogBlock(block);
    }

    internal static void DisableBlock(KeybindBlock block)
    {
        block.Active = false;
        // Rebuild while the block is still in Blocks so its ids are stripped from DefinedSettings (it is
        // inactive, so its settings are not re-added); then release the base so a reload can re-claim it.
        Rebuild();
        Blocks.Remove(block.BaseId);
        if (Blocks.Count == 0)
        {
            Unsubscribe();
        }
    }

    /// <summary>A binding/header was added to a block after it was already enabled; re-merge.</summary>
    internal static void OnBlockChanged(KeybindBlock block)
    {
        if (block.Active)
        {
            Rebuild();
        }
    }

    private static string CategoryLabel(SettingsCategory category)
    {
        bool english = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        return category switch
        {
            SettingsCategory.Gameplay => english ? "Gameplay" : "游戏玩法",
            SettingsCategory.Display => english ? "Display & Tags" : "显示与标签",
            SettingsCategory.Announcements => english ? "Announcements" : "公告与提示",
            SettingsCategory.Tools => english ? "Tools & Admin" : "工具与管理",
            _ => english ? "Other" : "其他",
        };
    }

    /// <summary>
    /// The registry canonical render order: category first (by the enum numeric order), then base id, so a
    /// category members are adjacent and the sequence is identical on every server regardless of the order
    /// plugins happened to load in. One category header is emitted the first time that category appears, and
    /// a category with no included block emits nothing.
    /// </summary>
    private static IEnumerable<ServerSpecificSettingBase> BuildOrdered(Func<KeybindBlock, bool> include, Player? player = null)
    {
        SettingsCategory? current = null;
        foreach (KeybindBlock block in Blocks.Values
                     .Where(include)
                     .OrderBy(block => (int)block.Category)
                     .ThenBy(block => block.SortOrder)
                     .ThenBy(block => block.BaseId))
        {
            if (current != block.Category)
            {
                current = block.Category;
                yield return new SSGroupHeader(SssIdBlocks.CategoryHeaderId(block.Category), CategoryLabel(block.Category));
            }

            foreach (ServerSpecificSettingBase setting in block.BuildSettings(player))
            {
                yield return setting;
            }
        }
    }

    /// <summary>
    /// Every id the registry may emit that no block owns: the synthesised category headers.
    ///
    /// Derived from the CLAIMED BLOCKS as well as the declared list, not from the declared list alone. A
    /// header whose category is no longer represented still has to be strippable, or Rebuild would leave
    /// the stale one in place and prepend a fresh one on every pass. <see cref="KeybindBlock.InCategory"/>
    /// rejects undeclared values, so in practice these agree - the union is belt and braces for a block
    /// claimed before that validation existed.
    /// </summary>
    private static IEnumerable<int> RegistryOwnedIds()
    {
        HashSet<SettingsCategory> categories = new(SssIdBlocks.AllCategories);
        foreach (KeybindBlock block in Blocks.Values)
        {
            categories.Add(block.Category);
        }

        foreach (SettingsCategory category in categories)
        {
            yield return SssIdBlocks.CategoryHeaderId(category);
        }
    }

    /// <summary>
    /// Rebuilds the shared <see cref="ServerSpecificSettingsSync.DefinedSettings"/>: strip every id this registry
    /// owns (across ALL claimed blocks, so a disabled block's stale entries are removed), re-add only ACTIVE blocks'
    /// settings, and preserve all foreign entries (additive merge). Then broadcast to everyone.
    /// </summary>
    private static void Rebuild()
    {
        HashSet<int> ownedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                ownedIds.Add(id);
            }
        }

        ActiveBindings.Clear();
        ActiveValueSettings.Clear();
        foreach (KeybindBlock block in Blocks.Values)
        {
            if (!block.Active)
            {
                continue;
            }

            foreach (KeyValuePair<int, KeybindBlock.Binding> pair in block.Bindings)
            {
                ActiveBindings[block.BaseId + pair.Key] = new ActiveBinding(block, pair.Value);
            }
            foreach (KeyValuePair<int, KeybindBlock.ValueSetting> pair in block.ValueSettings)
            {
                ActiveValueSettings[block.BaseId + pair.Key] = new ActiveValueSetting(block, pair.Value);
            }
        }

        List<ServerSpecificSettingBase> ours = BuildOrdered(block => block.Active).ToList();

        ServerSpecificSettingBase[] existingSettings = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        WarnOnForeignCollisions(existingSettings, ownedIds);

        // OURS FIRST, foreign entries after. Appending used to put every registry setting behind every
        // plugin that merges into DefinedSettings on its own (HUD toggles, music mutes), so the ability
        // keybinds - which nothing works without - sat at the very bottom of the menu no matter how the
        // categories were ordered. Foreign entries keep their own relative order and are otherwise
        // untouched; the registry is the sanctioned owner of this array for metarepo plugins.
        ServerSpecificSettingsSync.DefinedSettings = ours
            .Concat(existingSettings.Where(setting => !ownedIds.Contains(setting.SettingId)))
            .ToArray();
        SendPersonalizedToAll();
    }

    /// <summary>
    /// The starting position a two-button setting hands <paramref name="player"/>, or null when that id is
    /// not a registry-owned two-button setting. Consumers compare the value they RECEIVE against this to
    /// tell an explicit choice from an untouched default - the client reports its value on acquisition as
    /// well as on change, so a callback alone proves nothing.
    /// </summary>
    public static bool? DefaultTwoButtonsFor(Player player, int settingId)
    {
        if (player == null || !ActiveValueSettings.TryGetValue(settingId, out ActiveValueSetting active))
        {
            return null;
        }

        return active.Setting is KeybindBlock.TwoButtonsSetting twoButtons ? twoButtons.DefaultFor(player) : null;
    }

    /// <summary>Immediately re-sends the caller-specific visible settings collection to one player.</summary>
    public static void RefreshPlayer(Player player)
    {
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady)
        {
            return;
        }

        SendPersonalized(player);
    }

    private static void SendPersonalizedToAll()
    {
        foreach (Player player in Player.ReadyList)
        {
            if (player.IsPlayer && player.IsReady)
            {
                SendPersonalized(player);
            }
        }
    }

    private static void SendPersonalized(Player player)
    {
        HashSet<int> allOwnedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                allOwnedIds.Add(id);
            }
        }

        // Ordered per RECIPIENT, not once globally: a category whose only block is hidden from this player
        // must not leave a dangling header behind for them. Ours lead here too, matching Rebuild.
        List<ServerSpecificSettingBase> collection =
            BuildOrdered(block => block.Active && block.IsVisibleTo(player), player).ToList();
        collection.AddRange((ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>())
            .Where(setting => !allOwnedIds.Contains(setting.SettingId)));

        ServerSpecificSettingsSync.SendToPlayer(player.ReferenceHub, collection.ToArray());
    }

    private static void WarnOnForeignCollisions(ServerSpecificSettingBase[] existingSettings, HashSet<int> ownedIds)
    {
        foreach (ServerSpecificSettingBase setting in existingSettings)
        {
            if (ownedIds.Contains(setting.SettingId) || WarnedForeignIds.Contains(setting.SettingId))
            {
                continue;
            }

            foreach (KeybindBlock block in Blocks.Values)
            {
                if (SssIdBlocks.Contains(block.BaseId, setting.SettingId))
                {
                    WarnedForeignIds.Add(setting.SettingId);
                    Logger.Warn(
                        $"[ServerKeybinds] Foreign setting id {setting.SettingId} sits inside block '{block.Owner}' " +
                        $"({block.BaseId}-{block.BaseId + SssIdBlocks.BlockWidth - 1}); a non-migrated plugin may collide.");
                    break;
                }
            }
        }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _previousJoinFilter = ServerSpecificSettingsSync.SendOnJoinFilter;
        ServerSpecificSettingsSync.SendOnJoinFilter = SuppressNativeJoinSend;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValueReceived;
        PlayerAuthenticationManager.OnInstanceModeChanged += OnInstanceModeChanged;
    }

    private static void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        if (ServerSpecificSettingsSync.SendOnJoinFilter == SuppressNativeJoinSend)
        {
            ServerSpecificSettingsSync.SendOnJoinFilter = _previousJoinFilter;
        }
        _previousJoinFilter = null;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSettingValueReceived;
        PlayerAuthenticationManager.OnInstanceModeChanged -= OnInstanceModeChanged;
        Pressed.Clear();
    }

    private static void OnSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (ActiveValueSettings.TryGetValue(setting.SettingId, out ActiveValueSetting activeValue))
        {
            Player? valuePlayer = Player.Get(hub);
            if (valuePlayer == null || !activeValue.Block.IsVisibleTo(valuePlayer))
            {
                return;
            }

            try
            {
                activeValue.Setting.Invoke(valuePlayer, setting);
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] '{activeValue.Setting.Label}' change handler threw: {exception.GetBaseException().Message}");
            }
            return;
        }

        if (setting is not SSKeybindSetting keybind || !ActiveBindings.TryGetValue(setting.SettingId, out ActiveBinding activeBinding))
        {
            return;
        }

        if (!Pressed.TryGetValue(hub, out HashSet<int> pressed))
        {
            pressed = new HashSet<int>();
            Pressed[hub] = pressed;
        }

        // Fires on both press and release. Act on the rising edge (press) and, for bindings that want it,
        // the falling edge (release) - so a handler can implement hold-to-act behavior.
        if (!keybind.SyncIsPressed)
        {
            if (pressed.Remove(setting.SettingId))
            {
                Invoke(activeBinding, hub, released: true);
            }

            return;
        }

        if (!pressed.Add(setting.SettingId))
        {
            return;
        }

        Invoke(activeBinding, hub, released: false);
    }

    private static void Invoke(ActiveBinding active, ReferenceHub hub, bool released)
    {
        Player? player = Player.Get(hub);
        if (player == null)
        {
            return;
        }

        if (!active.Block.IsVisibleTo(player))
        {
            return;
        }

        KeybindBlock.Binding binding = active.Binding;
        Action<Player>? handler = released ? binding.OnReleased : binding.OnPressed;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(player);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] '{binding.Label}' {(released ? "release" : "press")} handler threw: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// When a client finishes authenticating, re-send the FULL (additive) DefinedSettings to it a beat later.
    /// Some plugins push only their own settings to a joining player (replacing the client's whole view and
    /// dropping everyone else's entries); re-broadcasting the complete shared set restores all of them.
    /// </summary>
    private static void OnInstanceModeChanged(ReferenceHub hub, ClientInstanceMode mode)
    {
        if (mode != ClientInstanceMode.ReadyClient)
        {
            return;
        }

        if (_previousJoinFilter != null && !_previousJoinFilter(hub))
        {
            return;
        }

        Timing.CallDelayed(0.75f, () =>
        {
            if (!_subscribed || hub == null || hub.connectionToClient == null)
            {
                return;
            }

            try
            {
                Player? player = Player.Get(hub);
                if (player != null)
                {
                    SendPersonalized(player);
                }
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] Failed to re-send settings to a player: {exception.GetBaseException().Message}");
            }
        });
    }

    private static void LogBlock(KeybindBlock block)
    {
        // Every entry, not just keybinds: a block that registers only a toggle used to log an empty list,
        // which reads exactly like "the setting failed to register".
        IEnumerable<string> binds = block.Bindings.Values
            .Select(b => $"{block.BaseId + b.Local}:{b.Label}({b.DefaultKey})");
        IEnumerable<string> values = block.ValueSettings.Values
            .Select(v => $"{block.BaseId + v.Local}:{v.Label}");
        IEnumerable<string> texts = block.Texts
            .Select(t => $"{block.BaseId + t.Local}:<text>");
        string entries = string.Join(", ", texts.Concat(binds).Concat(values));
        Logger.Info(
            $"[ServerKeybinds] '{block.Owner}' enabled block {block.BaseId} " +
            $"under {block.Category} [{entries}].");
    }

    private static bool SuppressNativeJoinSend(ReferenceHub _) => false;

    private readonly struct ActiveBinding
    {
        public ActiveBinding(KeybindBlock block, KeybindBlock.Binding binding)
        {
            Block = block;
            Binding = binding;
        }

        public KeybindBlock Block { get; }

        public KeybindBlock.Binding Binding { get; }
    }

    private readonly struct ActiveValueSetting
    {
        public ActiveValueSetting(KeybindBlock block, KeybindBlock.ValueSetting setting)
        {
            Block = block;
            Setting = setting;
        }

        public KeybindBlock Block { get; }

        public KeybindBlock.ValueSetting Setting { get; }
    }
}
