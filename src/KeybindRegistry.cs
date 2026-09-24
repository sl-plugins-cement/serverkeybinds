using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using MEC;
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
/// The registry owns every native entry type and the ORDER of the menu: blocks are grouped by
/// <see cref="SettingsCategory"/> under one synthesised group header each, so what the player sees does
/// not depend on plugin load order. It ships as a shared LIBRARY built from source by every consumer and
/// deployed exactly once per server, in the folder that port's LabAPI loader reads, so its statics are
/// shared. A plugin that hard-references it but is missing the DLL fails to load entirely rather than
/// running half-broken.
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

    /// <summary>
    /// What happens to settings that other code writes into <c>DefinedSettings</c> without registering a
    /// block. <see cref="ForeignSettingsPolicy.Block"/> (the default) strips them from the shared array and
    /// from every send, so they never reach a client and their responses fail native prevalidation; each
    /// stripped id is logged once and listed by <c>keybinds foreign</c>. <see cref="ForeignSettingsPolicy.Merge"/>
    /// keeps the old additive behaviour for a server that must run an unported plugin.
    /// </summary>
    public static ForeignSettingsPolicy ForeignPolicy
    {
        get => _foreignPolicy;
        set
        {
            if (_foreignPolicy == value)
            {
                return;
            }

            _foreignPolicy = value;
            if (Blocks.Count > 0)
            {
                Rebuild();
            }
        }
    }

    /// <summary>Every foreign setting id stripped so far this process, with a short description of the entry.</summary>
    public static IReadOnlyDictionary<int, string> BlockedForeignSettings => BlockedForeign;

    private static ForeignSettingsPolicy _foreignPolicy = ForeignSettingsPolicy.Block;
    private static readonly Dictionary<int, string> BlockedForeign = new();

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

    /// <summary>
    /// The personalised dropdown option lists each player was ACTUALLY sent, keyed by UserId, with one
    /// response latch per entry per send generation. Same rationale as <see cref="SentTwoButtonDefaults"/>:
    /// a response is only meaningful against what is on the player's screen.
    /// </summary>
    private static readonly Dictionary<string, PersonalizedDropdownLedger> SentPersonalizedDropdowns = new(StringComparer.Ordinal);
    private static readonly Stopwatch RefreshClock = Stopwatch.StartNew();

    /// <summary>
    /// The debounced, coalesced, rate-limited refresh budget, keyed by UserId. Its snapshot is the Player
    /// itself: the view is built and fingerprinted when a candidate is processed, and the actual send goes
    /// through <see cref="Delivery"/> like every other send, which records itself here.
    /// </summary>
    private static readonly SssRefreshCoordinator<string, Player> RefreshCoordinator = new(
        () => RefreshClock.Elapsed.TotalSeconds,
        FingerprintFor,
        SendCoordinatedRefresh,
        StringComparer.Ordinal);

    /// <summary>The process-wide interest router used to target invalidations, keyed by UserId.</summary>
    public static SssInterestIndex<string> InterestIndex { get; } = new(StringComparer.Ordinal);

    private static readonly HashSet<int> WarnedForeignIds = new();
    private static readonly SettingsDeliveryCoordinator Delivery = new(
        SendPersonalizedNow,
        HasPressedLatch,
        ReleasePressedForReconcile);
    private static bool _commandRegistered;
    private static bool _identityLogged;
    private static bool _subscribed;
    private static bool _refreshPumpScheduled;
    private static int _refreshPumpGeneration;
    private static double _refreshPumpDueSeconds;
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
    /// <summary>Re-merges now; used by the RA command after a policy change.</summary>
    internal static void RebuildNow()
    {
        if (Blocks.Count > 0)
        {
            Rebuild();
        }
    }

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
    /// <param name="recordSent">
    /// True only on the build that is actually sent to <paramref name="player"/>: records the two-button
    /// defaults and personalised dropdown option lists they receive. A diagnostic or fingerprint build
    /// passes false so it cannot overwrite the record of what is on the player's screen.
    /// </param>
    private static IEnumerable<ServerSpecificSettingBase> BuildOrdered(Func<KeybindBlock, bool> include, Player? player, bool recordSent)
    {
        string userId = player?.UserId ?? string.Empty;
        PersonalizedDropdownLedger? ledger = null;
        if (recordSent && player != null && !string.IsNullOrWhiteSpace(userId))
        {
            ledger = LedgerFor(userId);
            ledger.StartGeneration();
        }

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
                if (ledger != null)
                {
                    if (setting is SSTwoButtonsSetting twoButtons)
                    {
                        RecordSentDefault(userId, twoButtons.SettingId, twoButtons.DefaultIsB);
                    }
                    else if (setting is SSDropdownSetting dropdown
                        && block.ValueSettings.TryGetValue(dropdown.SettingId - block.BaseId, out KeybindBlock.ValueSetting owner)
                        && owner is KeybindBlock.PersonalizedDropdownSetting)
                    {
                        ledger.Record(dropdown.SettingId, dropdown.Options);
                    }
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

        List<ServerSpecificSettingBase> ours = BuildOrdered(block => block.Active, player: null, recordSent: false).ToList();

        ServerSpecificSettingBase[] existingSettings = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        ServerSpecificSettingBase[] foreign = existingSettings.Where(setting => !ownedIds.Contains(setting.SettingId)).ToArray();

        if (_foreignPolicy == ForeignSettingsPolicy.Block)
        {
            // The registry is the only sanctioned writer. Anything else in the array was put there by a
            // plugin that bypassed it; drop it so it never reaches a client and log it so it gets ported.
            NoteBlocked(foreign);
            ServerSpecificSettingsSync.DefinedSettings = ours.ToArray();
        }
        else
        {
            // Merge: OURS FIRST, foreign entries after, in their own relative order. Appending used to put
            // every registry setting behind every self-merging plugin, so the ability keybinds sat at the
            // very bottom of the menu no matter how the categories were ordered.
            WarnOnForeignCollisions(existingSettings, ownedIds);
            ServerSpecificSettingsSync.DefinedSettings = ours.Concat(foreign).ToArray();
        }

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
    /// Queues a personalised re-send for one player through the process-wide refresh budget: trailing
    /// 500 ms debounce, coalesced reasons, at least two seconds between sends, at most six sends per
    /// rolling minute, and skipped entirely when the view is identical to the last send. The send itself
    /// goes through the same delivery path as every other send. Use this for state-driven refreshes;
    /// <see cref="RefreshPlayer"/> stays the immediate, acknowledged re-send for operators.
    /// </summary>
    public static void RequestPlayerRefresh(Player player, string reason)
    {
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady ||
            string.IsNullOrWhiteSpace(player.UserId))
        {
            return;
        }

        EnsureTracked(player.UserId);
        RefreshCoordinator.Request(player.UserId, player, reason);
        ScheduleRefreshPump();
    }

    /// <summary>Registers which state domains can affect one player's view. Untracked players default to all.</summary>
    public static void SetPlayerInterests(Player player, SssInterest interests)
    {
        if (player != null && !string.IsNullOrWhiteSpace(player.UserId))
        {
            InterestIndex.Track(player.UserId, interests);
        }
    }

    /// <summary>
    /// Requests a refresh for <paramref name="player"/> if they registered an interest in
    /// <paramref name="changed"/>. Never fans out to other players. Returns whether a refresh was requested.
    /// </summary>
    public static bool InvalidatePlayer(Player player, SssInterest changed, string reason)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.UserId))
        {
            return false;
        }

        EnsureTracked(player.UserId);
        if (InterestIndex.ResolvePersonal(player.UserId, changed).Count == 0)
        {
            return false;
        }

        RequestPlayerRefresh(player, reason);
        return true;
    }

    /// <summary>
    /// Requests a refresh for the existing players affected by the one-to-two or two-to-one population
    /// boundary (those present in both sets who registered <see cref="SssInterest.PopulationBoundary"/>).
    /// A newcomer is intentionally left to its ordinary join send. Returns how many refreshes were requested.
    /// </summary>
    public static int InvalidatePopulationBoundary(
        IReadOnlyCollection<Player> before,
        IReadOnlyCollection<Player> after,
        string reason)
    {
        if (before == null)
        {
            throw new ArgumentNullException(nameof(before));
        }

        if (after == null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        Dictionary<string, Player> afterById = new(StringComparer.Ordinal);
        foreach (Player player in after)
        {
            if (player != null && !string.IsNullOrWhiteSpace(player.UserId))
            {
                afterById[player.UserId] = player;
            }
        }

        string[] beforeIds = before
            .Where(player => player != null && !string.IsNullOrWhiteSpace(player.UserId))
            .Select(player => player.UserId)
            .ToArray();
        int requested = 0;
        foreach (string userId in InterestIndex.ResolvePopulationBoundary(beforeIds, afterById.Keys.ToArray()))
        {
            if (afterById.TryGetValue(userId, out Player player))
            {
                RequestPlayerRefresh(player, reason);
                requested++;
            }
        }

        return requested;
    }

    /// <summary>Process-wide refresh counters (requested, sent, coalesced, rate-limited, identical).</summary>
    public static SssRefreshCounters RefreshCounters => RefreshCoordinator.Counters;

    /// <summary>The player's monotonic last-send time, last-sent view fingerprint and budget state.</summary>
    public static bool TryGetRefreshDiagnostics(Player player, out SssRefreshPlayerDiagnostics diagnostics)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.UserId))
        {
            diagnostics = default;
            return false;
        }

        return RefreshCoordinator.TryGetDiagnostics(player.UserId, out diagnostics);
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
    /// right now. Same build as <see cref="SendPersonalizedNow"/> (per-player entries may hide themselves,
    /// so the recipient matters) but without recording, so the record of what was ACTUALLY sent survives.
    /// </summary>
    internal static int PersonalizedEntryCountFor(Player player) => BuildCollection(player, recordSent: false).Count;

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

    /// <summary>
    /// The ONE send path. Every personalised send - join, rebuild, reconcile, acknowledgement retry,
    /// operator resend and budgeted refresh - is built and put on the wire here, and recorded in the
    /// refresh budget with the fingerprint of what was actually sent.
    /// </summary>
    private static int SendPersonalizedNow(Player player, string reason)
    {
        List<ServerSpecificSettingBase> collection = BuildCollection(player, recordSent: true);
        ServerSpecificSettingsSync.SendToPlayer(
            player.ReferenceHub,
            collection.ToArray(),
            SettingsDeliveryCoordinator.WireVersion);

        string userId = player.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            EnsureTracked(userId);
            RefreshCoordinator.RecordSent(userId, SssViewFingerprint.Compute(collection));
        }

        // LabAPI's Logger.Debug is NOT globally gated - always pass the flag or this spams every send.
        Logger.Debug($"[ServerKeybinds] Sent {collection.Count} entries to {player.Nickname} ({player.PlayerId}) [{reason}].", Debug);
        return collection.Count;
    }

    private static HashSet<int> AllOwnedIds()
    {
        HashSet<int> allOwnedIds = new(RegistryOwnedIds());
        foreach (KeybindBlock block in Blocks.Values)
        {
            foreach (int id in block.OwnedIds())
            {
                allOwnedIds.Add(id);
            }
        }

        return allOwnedIds;
    }

    /// <summary>The full collection <paramref name="player"/> receives: ours, ordered per recipient, then foreign entries.</summary>
    private static List<ServerSpecificSettingBase> BuildCollection(Player player, bool recordSent)
    {
        HashSet<int> allOwnedIds = AllOwnedIds();

        // Ordered per RECIPIENT, not once globally: a category whose only block is hidden from this player
        // must not leave a dangling header behind for them. Ours lead here too, matching Rebuild.
        List<ServerSpecificSettingBase> collection =
            BuildOrdered(block => block.Active && block.IsVisibleTo(player), player, recordSent).ToList();
        if (_foreignPolicy == ForeignSettingsPolicy.Block)
        {
            // A plugin that wrote into DefinedSettings after our last Rebuild is caught here, on the next
            // send of any kind, so the shared array never carries foreign entries for long.
            EnforceForeignPolicy(allOwnedIds);
            return collection;
        }

        collection.AddRange((ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>())
            .Where(setting => !allOwnedIds.Contains(setting.SettingId)));
        return collection;
    }

    /// <summary>Strips foreign entries from the shared array if any appeared since the last rebuild.</summary>
    private static void EnforceForeignPolicy(HashSet<int> ownedIds)
    {
        ServerSpecificSettingBase[] current = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        ServerSpecificSettingBase[] foreign = current.Where(setting => !ownedIds.Contains(setting.SettingId)).ToArray();
        if (foreign.Length == 0)
        {
            return;
        }

        NoteBlocked(foreign);
        ServerSpecificSettingsSync.DefinedSettings = current.Where(setting => ownedIds.Contains(setting.SettingId)).ToArray();
    }

    private static void NoteBlocked(ServerSpecificSettingBase[] foreign)
    {
        foreach (ServerSpecificSettingBase setting in foreign)
        {
            if (BlockedForeign.ContainsKey(setting.SettingId))
            {
                continue;
            }

            string description = $"{setting.GetType().Name} '{setting.Label}'";
            BlockedForeign[setting.SettingId] = description;
            Logger.Warn(
                $"[ServerKeybinds] Blocked foreign setting id {setting.SettingId} ({description}): it was written to " +
                "DefinedSettings without a registry block, so it is stripped and never sent. Port the plugin to " +
                "KeybindRegistry.ClaimBlock, or set ForeignPolicy = Merge to tolerate it. 'keybinds foreign' lists these.");
        }
    }

    /// <summary>The refresh coordinator's fingerprint of the view <paramref name="player"/> would receive now.</summary>
    private static string FingerprintFor(Player player)
    {
        if (player == null || player.IsDestroyed || !player.IsPlayer || !player.IsReady)
        {
            return string.Empty;
        }

        try
        {
            return SssViewFingerprint.Compute(BuildCollection(player, recordSent: false));
        }
        catch (Exception exception)
        {
            // Never mistaken for the last sent fingerprint, which is always a real hash, so a failed build
            // falls through to a send attempt (whose own failure is logged by the delivery coordinator).
            Logger.Debug($"[ServerKeybinds] Refresh fingerprint for {player.Nickname} ({player.PlayerId}) failed: {exception.GetBaseException().Message}", Debug);
            return string.Empty;
        }
    }

    private static bool SendCoordinatedRefresh(string userId, Player player, IReadOnlyCollection<string> reasons)
    {
        if (player == null || !string.Equals(player.UserId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        // The delivery coordinator refuses players who cannot be sent to and audits the send like any
        // other; SendPersonalizedNow records it in the refresh budget with the fingerprint that went out.
        return Delivery.Send(player, "refresh:" + string.Join("+", reasons), requireAcknowledgement: false);
    }

    /// <summary>
    /// Wakes the refresh coordinator when its earliest candidate is due. One delayed call at a time; a
    /// request that moves the earliest due time forward re-arms it, and the generation guard discards a
    /// stale wake-up after <see cref="Unsubscribe"/>.
    /// </summary>
    private static void ScheduleRefreshPump()
    {
        double? delay = RefreshCoordinator.SecondsUntilNextProcess();
        if (!delay.HasValue)
        {
            return;
        }

        double dueSeconds = RefreshClock.Elapsed.TotalSeconds + delay.Value;
        if (_refreshPumpScheduled && dueSeconds >= _refreshPumpDueSeconds - 0.001)
        {
            return;
        }

        _refreshPumpScheduled = true;
        _refreshPumpDueSeconds = dueSeconds;
        int generation = ++_refreshPumpGeneration;
        Timing.CallDelayed((float)Math.Max(0.01, delay.Value), () =>
        {
            if (generation != _refreshPumpGeneration)
            {
                return;
            }

            _refreshPumpScheduled = false;
            if (!_subscribed)
            {
                return;
            }

            RefreshCoordinator.ProcessDue();
            ScheduleRefreshPump();
        });
    }

    private static void EnsureTracked(string userId)
    {
        if (!InterestIndex.IsTracked(userId))
        {
            InterestIndex.Track(userId);
        }
    }

    private static PersonalizedDropdownLedger LedgerFor(string userId)
    {
        if (!SentPersonalizedDropdowns.TryGetValue(userId, out PersonalizedDropdownLedger ledger))
        {
            ledger = new PersonalizedDropdownLedger();
            SentPersonalizedDropdowns[userId] = ledger;
        }

        return ledger;
    }

    /// <summary>
    /// Interprets a personalised dropdown response against the option list this player was sent: the first
    /// value per send generation is an acquisition, a later different value a change, a repeat a duplicate,
    /// and anything outside the sent list (or with no sent list at all) is stale.
    /// </summary>
    internal static PersonalizedDropdownResponseOutcome TakePersonalizedDropdownResponse(
        Player player,
        int settingId,
        int rawIndex,
        out DropdownSelection selection,
        out PersonalizedDropdownResponseKind kind)
    {
        selection = default;
        kind = PersonalizedDropdownResponseKind.Duplicate;
        string userId = player?.UserId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) ||
            !SentPersonalizedDropdowns.TryGetValue(userId, out PersonalizedDropdownLedger ledger))
        {
            return PersonalizedDropdownResponseOutcome.Stale;
        }

        return ledger.Observe(settingId, rawIndex, out selection, out kind);
    }

    /// <summary>
    /// The client's view of a personalised dropdown does not match what this registry sent (a foreign
    /// send replaced it, the model moved on, or the packet was forged). Drop the response and re-send the
    /// current view through the refresh budget, which bounds how often a hostile client can trigger this.
    /// </summary>
    internal static void RejectStaleDropdownResponse(Player player, int settingId, string why)
    {
        Logger.Debug(
            $"[ServerKeybinds] Trace: stale dropdown response for id {settingId} from player {player.PlayerId} rejected ({why}); refresh requested.",
            PressTrace);
        RequestPlayerRefresh(player, "stale-dropdown-response");
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
        SentPersonalizedDropdowns.Clear();
        RefreshCoordinator.Clear();
        InterestIndex.Clear();
        _refreshPumpScheduled = false;
        _refreshPumpGeneration++;
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
        // Every client reconnects and gets a fresh join send, so per-player records start over too.
        SentTwoButtonDefaults.Clear();
        SentPersonalizedDropdowns.Clear();
        RefreshCoordinator.Clear();
        InterestIndex.Clear();
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
            SentPersonalizedDropdowns.Remove(userId);
            RefreshCoordinator.Remove(userId);
            InterestIndex.Untrack(userId);
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
            // LabAPI loads dependencies from bytes, so Location is usually empty and there is no file to hash;
            // the module version id is a per-compilation GUID that still identifies the exact build.
            Logger.Info(
                $"[ServerKeybinds] {selfName.Name} {selfName.Version} (API {ApiVersion}) loaded from {location}" +
                $"{HashSuffix(location)} mvid {ModuleIdOf(self)}.");

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
                    $"{otherLocation}{HashSuffix(otherLocation)} mvid {ModuleIdOf(other)}. Two copies mean two registries and two join-send " +
                    "owners; keep only the copy in the folder this port's LabAPI loader reads.");
            }
        }
        catch (Exception exception)
        {
            Logger.Debug($"[ServerKeybinds] Assembly identity check skipped: {exception.GetBaseException().Message}", Debug);
        }
    }

    private static string ModuleIdOf(Assembly assembly)
    {
        try
        {
            return assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
        }
        catch
        {
            return "unknown";
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

/// <summary>How <see cref="KeybindRegistry"/> treats settings written into <c>DefinedSettings</c> by anything other than a registered block.</summary>
public enum ForeignSettingsPolicy
{
    /// <summary>Strip them from the shared array and from every send; log each id once. The default.</summary>
    Block,

    /// <summary>Keep them after the registry's own entries, as the additive merge always did.</summary>
    Merge,
}
