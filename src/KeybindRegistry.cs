using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using PlayerRoles;
using RemoteAdmin;
using RoundRestarting;
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
/// API 4 owns keybinds, dropdowns, sliders, and two-button toggles, and it also owns the ORDER of the
/// menu: blocks are grouped by <see cref="SettingsCategory"/> under one synthesised group header each, so
/// what the player sees no longer depends on plugin load order. This ships as a dependency LIBRARY (deployed to LabAPI's <c>dependencies/global</c>), so it loads exactly
/// once before any plugin and its statics are shared. A plugin that hard-references it but is missing the DLL
/// fails to load entirely rather than running half-broken.
/// </summary>
public static class KeybindRegistry
{
    /// <summary>
    /// Diagnostic level of the contract, frozen at 6. It appears in the startup log so a deployed DLL can be
    /// identified; nothing branches on it and consumers must not probe it or document "requires API N".
    ///
    /// The public surface is additive-only: members are never removed or re-signatured, so a consumer built
    /// against any earlier level runs against this assembly. Every server runs the newest build. New native
    /// entry types are covered without a contract change through <see cref="KeybindBlock.AddNative"/>.
    /// </summary>
    public static int ApiVersion => 6;

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

    /// <summary>
    /// Gates the per-send audit log line (one <c>Logger.Debug</c> per personalized settings send, tagged
    /// with the reason) and the join-send skip diagnostics. Like <see cref="Language"/>, a consumer sets
    /// it from its own config; last writer wins.
    /// </summary>
    public static bool Debug { get; set; }

    /// <summary>
    /// Gates the press-trace log lines emitted at every keybind/value routing decision (received value
    /// swallowed, unknown id, latch outcomes, handler routing). Same consumer-set convention as
    /// <see cref="Language"/>; also toggleable at runtime via the <c>keybinds trace</c> RA command.
    /// </summary>
    public static bool PressTrace { get; set; }

    private static readonly Dictionary<int, KeybindBlock> Blocks = new();
    private static readonly Dictionary<int, ActiveBinding> ActiveBindings = new();
    private static readonly Dictionary<int, ActiveValueSetting> ActiveValueSettings = new();
    private static readonly Dictionary<string, HashSet<int>> Pressed = new(StringComparer.Ordinal);

    /// <summary>
    /// The two-button default each player was ACTUALLY sent, keyed by stable UserId then setting id.
    ///
    /// Recorded at serialisation rather than recomputed on demand. A per-player default is a function of
    /// live state - playtime, role, permissions - so asking the resolver again later can return a different
    /// answer than the one on the player's screen, and a consumer comparing against it would then read an
    /// untouched default as a deliberate choice. What was sent is the only thing worth comparing to.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<int, bool>> SentTwoButtonDefaults = new(StringComparer.Ordinal);
    private static readonly HashSet<int> WarnedForeignIds = new();
    private static readonly SettingsDeliveryCoordinator Delivery = new(
        SendPersonalizedNow,
        HasPressedLatch,
        ReleasePressedForReconcile);
    private static bool _commandRegistered;
    private static bool _identityLogged;
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
                if (player != null && setting is SSTwoButtonsSetting twoButtons)
                {
                    RecordSentDefault(player.UserId, twoButtons.SettingId, twoButtons.DefaultIsB);
                }

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

        ReleaseAllPressed("registry rebuild");
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
        if (player == null)
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(player.UserId) &&
            SentTwoButtonDefaults.TryGetValue(player.UserId, out Dictionary<int, bool> perSetting)
            && perSetting.TryGetValue(settingId, out bool sent)
                ? sent
                : (bool?)null;
    }

    private static void RecordSentDefault(string userId, int settingId, bool defaultIsB)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        if (!SentTwoButtonDefaults.TryGetValue(userId, out Dictionary<int, bool> perSetting))
        {
            perSetting = new Dictionary<int, bool>();
            SentTwoButtonDefaults[userId] = perSetting;
        }

        perSetting[settingId] = defaultIsB;
    }

    /// <summary>Immediately re-sends the caller-specific visible settings collection to one player.</summary>
    public static void RefreshPlayer(Player player)
    {
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady)
        {
            return;
        }

        Delivery.Send(player, "refresh", requireAcknowledgement: true);
    }

    /// <summary>
    /// The last personalized-send audit record for <paramref name="player"/>: when it happened (UTC) and
    /// how many entries it carried. False when no send has been recorded for them this round.
    /// </summary>
    public static bool TryGetSendAudit(Player player, out DateTime lastSendUtc, out int entryCount)
    {
        return Delivery.TryGetAudit(player, out lastSendUtc, out entryCount, out _);
    }

    internal static bool TryGetSendAuditDetails(
        Player player,
        out DateTime lastSendUtc,
        out int entryCount,
        out string reason) => Delivery.TryGetAudit(player, out lastSendUtc, out entryCount, out reason);

    internal static bool IsAwaitingAcknowledgement(Player player, out int attempt) =>
        Delivery.IsAwaitingAcknowledgement(player, out attempt);

    /// <summary>The setting ids currently latched as pressed for <paramref name="player"/> (snapshot).</summary>
    public static IReadOnlyCollection<int> PressedFor(Player player)
    {
        return player != null && !string.IsNullOrWhiteSpace(player.UserId) &&
            Pressed.TryGetValue(player.UserId, out HashSet<int> pressed)
            ? pressed.ToArray()
            : Array.Empty<int>();
    }

    /// <summary>
    /// Diagnostics only: the entry count <paramref name="player"/> WOULD receive from a personalized send
    /// right now. Mirrors <see cref="SendPersonalized"/> but builds with a null player so the
    /// <see cref="SentTwoButtonDefaults"/> record of what was ACTUALLY sent is not overwritten (the player
    /// only affects value defaults, never the entry count).
    /// </summary>
    internal static int PersonalizedEntryCountFor(Player player)
    {
        HashSet<int> allOwnedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                allOwnedIds.Add(id);
            }
        }

        return BuildOrdered(block => block.Active && block.IsVisibleTo(player)).Count()
            + (ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>())
                .Count(setting => !allOwnedIds.Contains(setting.SettingId));
    }

    private static void SendPersonalizedToAll()
    {
        foreach (Player player in Player.ReadyList)
        {
            if (player.IsPlayer && player.IsReady)
            {
                Delivery.Send(player, "rebuild", requireAcknowledgement: false);
            }
        }
    }

    private static int SendPersonalizedNow(Player player, string reason)
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

        ServerSpecificSettingsSync.SendToPlayer(
            player.ReferenceHub,
            collection.ToArray(),
            SettingsDeliveryCoordinator.WireVersion);
        // LabAPI's Logger.Debug is NOT globally gated - always pass the flag or this spams every send.
        Logger.Debug($"[ServerKeybinds] Sent {collection.Count} entries to {player.Nickname} ({player.PlayerId}) [{reason}].", Debug);
        return collection.Count;
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
        LogAssemblyIdentityOnce();
        _previousJoinFilter = ServerSpecificSettingsSync.SendOnJoinFilter;
        ServerSpecificSettingsSync.SendOnJoinFilter = SuppressNativeJoinSend;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValueReceived;
        PlayerRoleManager.OnRoleChanged += OnRoleChanged;
        RoundRestart.OnRestartTriggered += OnRoundRestart;
        ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;
        Delivery.Start();

        if (!_commandRegistered)
        {
            // LabAPI only scans PLUGIN assemblies for [CommandHandler] types; a dependencies/global
            // library must self-register. Registered once for the process lifetime - TryRegisterCommand
            // no-ops (returning true) on a duplicate name, so a later re-subscribe cannot double-add.
            _commandRegistered = CommandLoader.TryRegisterCommand(
                new KeybindsCommand(), CommandProcessor.RemoteAdminCommandHandler, "ServerKeybinds");
            if (!_commandRegistered)
            {
                Logger.Warn("[ServerKeybinds] 'keybinds' RA command could not be registered (name already taken?).");
            }
            else
            {
                Logger.Info("[ServerKeybinds] Registered RA command 'keybinds' (alias 'skb').");
            }
        }
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
        PlayerRoleManager.OnRoleChanged -= OnRoleChanged;
        RoundRestart.OnRestartTriggered -= OnRoundRestart;
        ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;
        Delivery.Stop();
        Pressed.Clear();
        SentTwoButtonDefaults.Clear();
    }

    private static void OnRoleChanged(ReferenceHub userHub, PlayerRoleBase prevRole, PlayerRoleBase newRole)
    {
        // A role change invalidates any held key: the client keybind state resets with the role, so a
        // latched press would otherwise never see its falling edge and block the next rising one.
        // Dispatch the release BEFORE dropping the latch - every delivered press must be followed by
        // exactly one release, or hold-to-act consumers without their own role hook stay stuck held.
        ReleasePressed(userHub, "role change");
    }

    private static void OnRoundRestart()
    {
        Pressed.Clear();
        Delivery.ResetRound();
    }

    private static void OnPlayerRemoved(ReferenceHub hub)
    {
        // Fires from ReferenceHub.OnDestroy while the hub is still valid, so removing the hub-keyed entry
        // here also closes the destroyed-hub-key hazard (ReferenceHub.GetHashCode derefs its gameObject).
        if (hub == null)
        {
            return;
        }

        string userId = UserIdOf(hub);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            Pressed.Remove(userId);
            SentTwoButtonDefaults.Remove(userId);
        }
    }

    private static void OnSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (ActiveValueSettings.TryGetValue(setting.SettingId, out ActiveValueSetting activeValue))
        {
            Player? valuePlayer = Player.Get(hub);
            if (valuePlayer == null || !activeValue.Block.IsVisibleTo(valuePlayer))
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: value for id {setting.SettingId} from player {hub.PlayerId} swallowed: block '{activeValue.Block.Owner}' not visible.", PressTrace);
                }
                return;
            }

            Delivery.AcknowledgeInput(hub);
            try
            {
                activeValue.Setting.Invoke(valuePlayer, setting);
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] '{activeValue.Setting.Label}' change handler threw: {exception.GetBaseException()}");
            }
            return;
        }

        if (setting is not SSKeybindSetting keybind)
        {
            return;
        }

        if (!ActiveBindings.TryGetValue(setting.SettingId, out ActiveBinding activeBinding))
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: no active binding for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
            }
            return;
        }

        Player? keyPlayer = Player.Get(hub);
        if (keyPlayer == null || !activeBinding.Block.IsVisibleTo(keyPlayer))
        {
            Logger.Debug(
                $"[ServerKeybinds] Trace: keybind for id {setting.SettingId} from player {hub.PlayerId} swallowed: " +
                $"block '{activeBinding.Block.Owner}' not visible.",
                PressTrace);
            return;
        }

        string userId = keyPlayer.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        Delivery.AcknowledgeInput(hub);
        if (!Pressed.TryGetValue(userId, out HashSet<int> pressed))
        {
            pressed = new HashSet<int>();
            Pressed[userId] = pressed;
        }

        // Fires on both press and release. Act on the rising edge (press) and, for bindings that want it,
        // the falling edge (release) - so a handler can implement hold-to-act behavior.
        if (!keybind.SyncIsPressed)
        {
            if (pressed.Remove(setting.SettingId))
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: release for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
                }
                Invoke(activeBinding, hub, released: true);
            }
            else
            {
                if (PressTrace)
                {
                    Logger.Debug($"[ServerKeybinds] Trace: release with no latch for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
                }
            }

            return;
        }

        if (!pressed.Add(setting.SettingId))
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: press already latched (ignored) for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
            }
            return;
        }

        if (PressTrace)
        {
            Logger.Debug($"[ServerKeybinds] Trace: press latched for id {setting.SettingId} (player {hub.PlayerId}).", PressTrace);
        }
        Invoke(activeBinding, hub, released: false);
    }

    private static void Invoke(ActiveBinding active, ReferenceHub hub, bool released, bool requireVisibility = true)
    {
        Player? player = Player.Get(hub);
        if (player == null)
        {
            return;
        }

        if (requireVisibility && !active.Block.IsVisibleTo(player))
        {
            return;
        }

        KeybindBlock.Binding binding = active.Binding;
        if (PressTrace)
        {
            Logger.Debug($"[ServerKeybinds] Trace: routing {(released ? "release" : "press")} to owner='{active.Block.Owner}' binding='{binding.Label}' (player {hub.PlayerId}).", PressTrace);
        }
        Action<Player>? handler = released ? binding.OnReleased : binding.OnPressed;
        if (handler == null)
        {
            if (PressTrace)
            {
                Logger.Debug($"[ServerKeybinds] Trace: no handler for this edge on binding '{binding.Label}'.", PressTrace);
            }
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

    private static bool HasPressedLatch(Player player) =>
        player != null && !string.IsNullOrWhiteSpace(player.UserId) &&
        Pressed.TryGetValue(player.UserId, out HashSet<int> pressed) && pressed.Count > 0;

    private static void ReleasePressedForReconcile(Player player, string reason)
    {
        if (player?.ReferenceHub != null)
        {
            ReleasePressed(player.ReferenceHub, reason);
        }
    }

    private static void ReleasePressed(ReferenceHub hub, string reason)
    {
        string userId = UserIdOf(hub);
        if (hub == null || string.IsNullOrWhiteSpace(userId) ||
            !Pressed.TryGetValue(userId, out HashSet<int> pressed))
        {
            return;
        }

        int[] latchedSettingIds = pressed.ToArray();
        // Match the normal wire-release order: remove the latch before invoking consumers. A release
        // handler may disable/rebuild its block, whose forced cleanup must not redispatch this edge.
        Pressed.Remove(userId);
        foreach (int settingId in latchedSettingIds)
        {
            if (ActiveBindings.TryGetValue(settingId, out ActiveBinding activeBinding))
            {
                Logger.Debug(
                    $"[ServerKeybinds] Trace: {reason} releases latched id {settingId} (player {hub.PlayerId}).",
                    PressTrace);
                // This press already passed visibility when it was accepted. Cleanup must reach the
                // consumer even if a role change made the block invisible before the falling edge.
                Invoke(activeBinding, hub, released: true, requireVisibility: false);
            }
        }
    }

    private static void ReleaseAllPressed(string reason)
    {
        foreach (KeyValuePair<string, HashSet<int>> entry in Pressed.ToArray())
        {
            Player? player = Player.Get(entry.Key);
            ReferenceHub? hub = player?.ReferenceHub;
            if (hub == null)
            {
                continue;
            }

            ReleasePressed(hub, reason);
        }

        Pressed.Clear();
    }

    private static string UserIdOf(ReferenceHub hub) => hub?.authManager?.UserId ?? string.Empty;

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

    /// <summary>
    /// Logs which build this is and where it was loaded from, and shouts if a second ServerKeybinds assembly
    /// is loaded in the process. Two copies (a stale global one beside the port one, or a fork beside the
    /// mainline) mean two registries each believing it owns the join send; every such incident so far was
    /// found late from behaviour, so surface it at the first Enable instead.
    /// </summary>
    private static void LogAssemblyIdentityOnce()
    {
        if (_identityLogged)
        {
            return;
        }

        _identityLogged = true;
        try
        {
            Assembly self = typeof(KeybindRegistry).Assembly;
            AssemblyName selfName = self.GetName();
            string location = LocationOf(self);
            Logger.Info(
                $"[ServerKeybinds] {selfName.Name} {selfName.Version} (API {ApiVersion}) loaded from {location}" +
                $"{HashSuffix(location)}.");

            foreach (Assembly other in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(other, self) ||
                    !string.Equals(other.GetName().Name, selfName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string otherLocation = LocationOf(other);
                Logger.Error(
                    $"[ServerKeybinds] A second {selfName.Name} assembly ({other.GetName().Version}) is loaded from " +
                    $"{otherLocation}{HashSuffix(otherLocation)}. Two copies mean two registries and two join-send " +
                    "owners; keep only the copy in the folder this port's LabAPI loader reads.");
            }
        }
        catch (Exception exception)
        {
            Logger.Debug($"[ServerKeybinds] Assembly identity check skipped: {exception.GetBaseException().Message}", Debug);
        }
    }

    private static string LocationOf(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location) ? "(in-memory)" : assembly.Location;
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string HashSuffix(string location)
    {
        if (!File.Exists(location))
        {
            return string.Empty;
        }

        try
        {
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(location);
            return " sha256 " + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

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
