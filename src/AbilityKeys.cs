using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace ServerKeybinds;

/// <summary>A shared ability key. The player's current role decides what it does.</summary>
public enum AbilityKey
{
    PrimaryUse,
    SecondaryUse,
    Ability1,
    Ability2,
    Ability3,
}

/// <summary>
/// The ability keys every role shares: Primary Use, Secondary Use, Ability 1-3 and a held modifier.
/// A player holds one role at a time, so one small set of keys serves every custom SCP and human role,
/// and each needs binding once per server. Native keybinds start unassigned, so every extra key is a
/// setup step for new players.
///
/// The block keeps the ids and types reinforcements-system shipped (1090001/1090002, 1090004-1090007).
/// The client saves a key under <c>SrvSp_&lt;server&gt;_&lt;typeCode&gt;_&lt;settingId&gt;</c>, so those
/// existing bindings carry over unchanged.
///
/// Acquire one lease per enabled consumer and dispose it on disable. A press goes to consumers in
/// priority order (then acquisition order) until one returns true; that consumer receives the matching
/// release. Consumers must return false for players whose current role they do not own. Call lifecycle
/// methods on the game thread.
/// </summary>
public static class AbilityKeys
{
    public const int PrimaryUseId = SssIdBlocks.AbilityKeys + PrimaryLocal;
    public const int SecondaryUseId = SssIdBlocks.AbilityKeys + SecondaryLocal;
    public const int SlotCount = 3;
    public const int FirstSlotId = SssIdBlocks.AbilityKeys + FirstSlotLocal;
    public const int ModifierId = SssIdBlocks.AbilityKeys + ModifierLocal;

    private const int PrimaryLocal = 1;
    private const int SecondaryLocal = 2;
    private const int FirstSlotLocal = 4;
    private const int ModifierLocal = FirstSlotLocal + SlotCount;
    private const int HelpLocal = 9;
    private const string GuiRefreshReason = "ability-keys-gui";

    private static readonly KeyCode[] SlotSuggestions = { KeyCode.Z, KeyCode.X, KeyCode.T };
    private static readonly List<Lease> Leases = new();
    private static readonly Dictionary<(int PlayerId, AbilityKey Key), Lease> Owners = new();
    private static readonly HashSet<int> ModifierHeld = new();
    private static KeybindBlock? _block;
    private static int _sequence;

    /// <summary>
    /// Registers a consumer. <paramref name="onDown"/> receives the player, the key and whether the
    /// modifier is held, and returns true when it handled the press. <paramref name="extras"/> adds the
    /// consumer's own dedicated controls under the same menu group; they keep any id they already
    /// shipped in this block. Higher <paramref name="priority"/> is offered presses first.
    /// <paramref name="keysWorkInGui"/> marks players whose role uses the Ability keys and modifier inside
    /// a native GUI, such as the SCP-079 camera interface; everyone else's are ignored while a menu is open.
    /// </summary>
    public static IDisposable Acquire(
        string owner,
        Func<Player, AbilityKey, bool, bool> onDown,
        Action<Player, AbilityKey>? onUp = null,
        Action<AbilityKeyExtras>? extras = null,
        int priority = 0,
        Func<Player, bool>? keysWorkInGui = null)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        if (onDown == null) throw new ArgumentNullException(nameof(onDown));

        var collected = new AbilityKeyExtras(owner);
        extras?.Invoke(collected);
        foreach (Lease other in Leases)
        {
            collected.EnsureDisjoint(other.Extras);
        }

        var lease = new Lease(owner, onDown, onUp, collected, priority, _sequence++, keysWorkInGui);
        Leases.Add(lease);
        Leases.Sort(static (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Sequence.CompareTo(b.Sequence));

        if (_block == null)
        {
            try { BuildBlock(); }
            catch { Leases.Remove(lease); throw; }
            PlayerEvents.Left += OnLeft;
            PlayerEvents.Dying += OnDying;
            PlayerEvents.ChangedRole += OnChangedRole;
            ServerEvents.RoundRestarted += OnRoundRestarted;
        }
        else
        {
            collected.ApplyTo(_block);
        }

        if (keysWorkInGui != null) RequestGuiRefreshForAll();
        return lease;
    }

    /// <summary>The absolute setting id of <paramref name="key"/>, e.g. to explain it in a guide.</summary>
    public static int SettingId(AbilityKey key) => key switch
    {
        AbilityKey.PrimaryUse => PrimaryUseId,
        AbilityKey.SecondaryUse => SecondaryUseId,
        _ => FirstSlotId + SlotIndex(key),
    };

    /// <summary>The 0-based slot of an Ability key, or -1 for Primary/Secondary Use.</summary>
    public static int SlotIndex(AbilityKey key) => key >= AbilityKey.Ability1 ? key - AbilityKey.Ability1 : -1;

    /// <summary>The Ability key for a 0-based slot.</summary>
    public static AbilityKey Slot(int index)
    {
        if (index < 0 || index >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        return AbilityKey.Ability1 + index;
    }

    /// <summary>True while the player holds the ability modifier.</summary>
    public static bool IsModifierHeld(Player? player) => player != null && ModifierHeld.Contains(player.PlayerId);

    private static void BuildBlock()
    {
        KeybindBlock block = KeybindRegistry.ClaimBlock(SssIdBlocks.AbilityKeys, "Ability Keys")
            .InCategory(SettingsCategory.Gameplay)
            // Pinned to the top: no role ability works until these keys are bound.
            .Order(-1000)
            .Header("技能按键")
            .AddTextArea(
                HelpLocal,
                "<size=21><color=#EAF6FF>这些键对所有角色通用，当前角色决定每个键的作用（SCP 技能、增援部队能力卡等），只需绑定一次。</color>\n" +
                "<color=#FFCB6B>显示 KEY NOT ASSIGNED 就是还没绑定：点一下建议按键即可采纳。</color></size>")
            .Add(PrimaryLocal, "主要使用", KeyCode.Mouse0,
                "使用当前角色的主要技能，或手中特殊装备的主要动作。",
                player => Down(player, AbilityKey.PrimaryUse), player => Up(player, AbilityKey.PrimaryUse))
            .Add(SecondaryLocal, "次要使用", KeyCode.Mouse1,
                "使用当前技能或手中特殊装备的次要动作（部分需要按住）。",
                player => Down(player, AbilityKey.SecondaryUse), player => Up(player, AbilityKey.SecondaryUse));

        for (int slot = 0; slot < SlotCount; slot++)
        {
            AbilityKey key = Slot(slot);
            block.Add(FirstSlotLocal + slot, $"技能 {slot + 1}", SlotSuggestions[slot],
                $"当前角色的第 {slot + 1} 个技能。按住技能修饰键则触发它的次要动作。",
                player => Down(player, key), player => Up(player, key), IgnoreInGui);
        }

        block.Add(ModifierLocal, "技能修饰键（按住）", KeyCode.LeftControl,
            "按住时，技能 1/2/3 触发次要动作。",
            OnModifierDown, OnModifierUp, IgnoreInGui);

        foreach (Lease lease in Leases)
        {
            lease.Extras.ApplyTo(block);
        }

        try { block.Enable(); }
        catch { if (block.Active) block.Disable(); throw; }
        _block = block;
    }

    private static bool IgnoreInGui(Player? player) => player == null || !KeysWorkInGui(player);

    private static bool KeysWorkInGui(Player player)
    {
        foreach (Lease lease in Leases)
        {
            try
            {
                if (lease.KeysWorkInGui?.Invoke(player) == true) return true;
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] Ability key GUI predicate of '{lease.Owner}' threw: {exception.GetBaseException().Message}");
            }
        }

        return false;
    }

    private static bool AnyGuiPredicate()
    {
        foreach (Lease lease in Leases)
        {
            if (lease.KeysWorkInGui != null) return true;
        }

        return false;
    }

    // The GUI flag is part of the sent entry and may follow the role, so a role change can need a re-send.
    // The registry compares the view with the last send and skips an identical one, which also covers a
    // player whose join send already carried the new role's flag.
    private static void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        Player? player = ev.Player;
        if (player == null || !player.IsReady || !AnyGuiPredicate()) return;
        KeybindRegistry.RequestPlayerRefresh(player, GuiRefreshReason);
    }

    // A GUI predicate arriving or leaving can change the flag for players already holding their role.
    private static void RequestGuiRefreshForAll()
    {
        foreach (Player player in Player.ReadyList)
        {
            KeybindRegistry.RequestPlayerRefresh(player, GuiRefreshReason);
        }
    }

    private static void Down(Player player, AbilityKey key)
    {
        if (player == null) return;
        Owners.Remove((player.PlayerId, key));
        bool modifier = ModifierHeld.Contains(player.PlayerId);
        foreach (Lease lease in Leases.ToArray())
        {
            bool handled;
            try
            {
                handled = lease.OnDown(player, key, modifier);
            }
            catch (Exception exception)
            {
                Logger.Warn($"[ServerKeybinds] Ability key {key} press handler of '{lease.Owner}' threw: {exception.GetBaseException()}");
                continue;
            }

            if (handled)
            {
                Owners[(player.PlayerId, key)] = lease;
                Logger.Debug($"[ServerKeybinds] Trace: ability key {key} (player {player.PlayerId}) handled by '{lease.Owner}'.", KeybindRegistry.PressTrace);
                return;
            }
        }

        Logger.Debug($"[ServerKeybinds] Trace: ability key {key} (player {player.PlayerId}) not handled.", KeybindRegistry.PressTrace);
    }

    private static void Up(Player player, AbilityKey key)
    {
        if (player == null || !Owners.TryGetValue((player.PlayerId, key), out Lease lease)) return;
        Owners.Remove((player.PlayerId, key));
        InvokeUp(lease, player, key);
    }

    private static void InvokeUp(Lease lease, Player player, AbilityKey key)
    {
        try
        {
            lease.OnUp?.Invoke(player, key);
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] Ability key {key} release handler of '{lease.Owner}' threw: {exception.GetBaseException()}");
        }
    }

    private static void OnModifierDown(Player player)
    {
        if (player != null) ModifierHeld.Add(player.PlayerId);
    }

    private static void OnModifierUp(Player player)
    {
        if (player != null) ModifierHeld.Remove(player.PlayerId);
    }

    private static void Forget(Player? player)
    {
        if (player == null) return;
        ModifierHeld.Remove(player.PlayerId);
        foreach (AbilityKey key in (AbilityKey[])Enum.GetValues(typeof(AbilityKey)))
        {
            Up(player, key);
        }
    }

    // The registry already releases latched keys on role change; death keeps the role briefly and a
    // leaving player gets no release, so both settle owed releases here.
    private static void OnDying(PlayerDyingEventArgs ev) => Forget(ev.Player);

    private static void OnLeft(PlayerLeftEventArgs ev) => Forget(ev.Player);

    private static void OnRoundRestarted()
    {
        Owners.Clear();
        ModifierHeld.Clear();
    }

    // Settles every press a departing consumer still holds. Once its ownership is gone, neither the
    // player's own release nor the registry's forced release (rebuild, role change) can reach it.
    private static void SettleReleases(Lease lease)
    {
        var owed = new List<(int PlayerId, AbilityKey Key)>();
        foreach (KeyValuePair<(int PlayerId, AbilityKey Key), Lease> pair in Owners)
        {
            if (ReferenceEquals(pair.Value, lease)) owed.Add(pair.Key);
        }

        foreach ((int PlayerId, AbilityKey Key) owned in owed)
        {
            // A release handler may itself have settled or reassigned this key.
            if (!Owners.TryGetValue(owned, out Lease current) || !ReferenceEquals(current, lease)) continue;
            Owners.Remove(owned);
            Player? player = Player.Get(owned.PlayerId);
            if (player != null) InvokeUp(lease, player, owned.Key);
        }
    }

    private static void Release(Lease lease)
    {
        if (!Leases.Remove(lease)) return;
        SettleReleases(lease);

        if (Leases.Count == 0 || lease.Extras.Count > 0)
        {
            // Entries cannot be removed from a live block, so a departing consumer's extras need a rebuild.
            _block?.Disable();
            _block = null;
        }

        if (Leases.Count == 0)
        {
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.Dying -= OnDying;
            PlayerEvents.ChangedRole -= OnChangedRole;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            Owners.Clear();
            ModifierHeld.Clear();
            return;
        }

        if (_block == null) BuildBlock();
        if (lease.KeysWorkInGui != null) RequestGuiRefreshForAll();
    }

    private sealed class Lease : IDisposable
    {
        private bool _disposed;

        public Lease(string owner, Func<Player, AbilityKey, bool, bool> onDown, Action<Player, AbilityKey>? onUp,
            AbilityKeyExtras extras, int priority, int sequence, Func<Player, bool>? keysWorkInGui)
        {
            KeysWorkInGui = keysWorkInGui;
            Owner = owner;
            OnDown = onDown;
            OnUp = onUp;
            Extras = extras;
            Priority = priority;
            Sequence = sequence;
        }

        public string Owner { get; }
        public Func<Player, AbilityKey, bool, bool> OnDown { get; }
        public Action<Player, AbilityKey>? OnUp { get; }
        public AbilityKeyExtras Extras { get; }
        public int Priority { get; }
        public int Sequence { get; }
        public Func<Player, bool>? KeysWorkInGui { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(this);
        }
    }
}

/// <summary>
/// A consumer's own controls inside the shared ability block, for a control a shared key cannot cover
/// (for example a hold key used while an ability key is held). Use the id you already shipped in this
/// block so saved choices survive; new controls take an unused local.
/// </summary>
public sealed class AbilityKeyExtras
{
    private static readonly HashSet<int> ReservedLocals = new() { 0, 1, 2, 4, 5, 6, 7, 9 };
    private readonly string _owner;
    private readonly List<Action<KeybindBlock>> _entries = new();
    private readonly HashSet<int> _locals = new();

    internal AbilityKeyExtras(string owner) => _owner = owner;

    internal int Count => _entries.Count;

    /// <summary>The absolute setting id of <paramref name="local"/> in the shared block.</summary>
    public int SettingId(int local) => SssIdBlocks.AbilityKeys + local;

    public AbilityKeyExtras AddTextArea(int local, string content)
    {
        Reserve(local);
        _entries.Add(block => block.AddTextArea(local, content));
        return this;
    }

    public AbilityKeyExtras Add(int local, string label, KeyCode suggestion, string hint,
        Action<Player> onPressed, Action<Player>? onReleased = null)
    {
        Reserve(local);
        _entries.Add(block => block.Add(local, label, suggestion, hint, onPressed, onReleased));
        return this;
    }

    public AbilityKeyExtras AddTwoButtons(int local, string label, string optionA, string optionB,
        bool defaultIsB, string hint, Action<Player, bool> onChanged)
    {
        Reserve(local);
        _entries.Add(block => block.AddTwoButtons(local, label, optionA, optionB, defaultIsB, hint, onChanged));
        return this;
    }

    internal void ApplyTo(KeybindBlock block)
    {
        foreach (Action<KeybindBlock> entry in _entries) entry(block);
    }

    internal void EnsureDisjoint(AbilityKeyExtras other)
    {
        foreach (int local in _locals)
        {
            if (other._locals.Contains(local))
            {
                throw new InvalidOperationException(
                    $"Ability key local {local} requested by '{_owner}' is already used by '{other._owner}'.");
            }
        }
    }

    private void Reserve(int local)
    {
        if (local <= 0 || local >= SssIdBlocks.BlockWidth || ReservedLocals.Contains(local))
        {
            throw new ArgumentOutOfRangeException(nameof(local), local, "This local is reserved by the shared ability keys.");
        }

        if (!_locals.Add(local))
        {
            throw new ArgumentException($"Local {local} is used twice by '{_owner}'.", nameof(local));
        }
    }
}
