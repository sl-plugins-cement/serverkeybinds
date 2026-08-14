using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;
using UserSettings.ServerSpecific;

namespace ServerKeybinds;

/// <summary>
/// One plugin's claimed 1000-wide id block. Build it fluently with headers, keybinds, dropdowns, and sliders,
/// then <see cref="Enable"/> in the plugin's Enable and <see cref="Disable"/> in its Disable. Local ids are
/// 0..999 within the block; 0 is conventionally the group header. Obtain one via
/// <see cref="KeybindRegistry.ClaimBlock"/>.
/// </summary>
public sealed class KeybindBlock
{
    internal readonly int BaseId;
    internal readonly string Owner;
    internal readonly List<HeaderEntry> Headers = new();
    internal readonly Dictionary<int, Binding> Bindings = new();
    internal readonly Dictionary<int, ValueSetting> ValueSettings = new();
    internal Func<Player, bool>? VisibilityFilter;
    internal bool Active;

    internal KeybindBlock(int baseId, string owner)
    {
        BaseId = baseId;
        Owner = owner;
    }

    /// <summary>Adds the block's group header at local id 0 (shown above its settings in the SSS menu).</summary>
    public KeybindBlock Header(string groupName) => Header(0, groupName);

    /// <summary>
    /// Restricts this entire block to players accepted by <paramref name="predicate"/>. Hidden players
    /// receive no entries from the block and their forged responses are ignored server-side.
    /// </summary>
    public KeybindBlock VisibleTo(Func<Player, bool> predicate)
    {
        VisibilityFilter = predicate ?? throw new ArgumentNullException(nameof(predicate));
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Adds a group header at an explicit local id (for a block that hosts several visual groups).</summary>
    public KeybindBlock Header(int local, string groupName)
    {
        ValidateLocal(local);
        Headers.Add(new HeaderEntry(local, groupName));
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Registers a keybind at <paramref name="local"/> (1..999). <paramref name="onPressed"/> fires on the
    /// rising edge; the optional <paramref name="onReleased"/> fires on the falling edge (for hold-to-act).
    /// </summary>
    public KeybindBlock Add(
        int local,
        string label,
        KeyCode defaultKey,
        string hint,
        Action<Player> onPressed,
        Action<Player>? onReleased = null,
        bool preventInteractionOnGui = true,
        bool allowSpectatorTrigger = false)
    {
        ValidateLocal(local);
        if (local == 0)
        {
            throw new ArgumentException("Local id 0 is reserved for the group header; use Header(name).", nameof(local));
        }

        if (IsLocalUsed(local))
        {
            throw new ArgumentException($"Local id {local} already used in block '{Owner}' ({BaseId}).", nameof(local));
        }

        Bindings[local] = new Binding(local, label, defaultKey, hint, onPressed, onReleased, preventInteractionOnGui, allowSpectatorTrigger);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Registers a shared-registry dropdown and invokes <paramref name="onChanged"/> with its validated index.</summary>
    public KeybindBlock AddDropdown(
        int local,
        string label,
        string[] options,
        int defaultIndex,
        string hint,
        Action<Player, int> onChanged,
        SSDropdownSetting.DropdownEntryType entryType = SSDropdownSetting.DropdownEntryType.ScrollableLoop)
    {
        ValidateAvailableValueLocal(local);
        if (options == null || options.Length == 0)
        {
            throw new ArgumentException("A dropdown must contain at least one option.", nameof(options));
        }

        ValueSettings[local] = new DropdownSetting(
            local, label, options, Mathf.Clamp(defaultIndex, 0, options.Length - 1), entryType, hint, onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Registers a shared-registry slider and invokes <paramref name="onChanged"/> with its validated value.</summary>
    public KeybindBlock AddSlider(
        int local,
        string label,
        float minValue,
        float maxValue,
        float defaultValue,
        bool integer,
        string valueToStringFormat,
        string finalDisplayFormat,
        string hint,
        Action<Player, float> onChanged)
    {
        ValidateAvailableValueLocal(local);
        if (maxValue < minValue)
        {
            throw new ArgumentException("Slider maximum must be greater than or equal to its minimum.", nameof(maxValue));
        }

        ValueSettings[local] = new SliderSetting(
            local, label, minValue, maxValue, Mathf.Clamp(defaultValue, minValue, maxValue), integer,
            valueToStringFormat, finalDisplayFormat, hint, onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>The absolute SSS id for a local id (base + local) — e.g. for sibling services rendering key glyphs.</summary>
    public int SettingId(int local)
    {
        ValidateLocal(local);
        return BaseId + local;
    }

    /// <summary>Merges this block's headers and settings into the shared <c>DefinedSettings</c> and broadcasts.</summary>
    public void Enable() => KeybindRegistry.EnableBlock(this);

    /// <summary>Removes this block's settings from the shared <c>DefinedSettings</c> and broadcasts.</summary>
    public void Disable() => KeybindRegistry.DisableBlock(this);

    internal IEnumerable<ServerSpecificSettingBase> BuildSettings()
    {
        foreach (HeaderEntry header in Headers)
        {
            yield return new SSGroupHeader(BaseId + header.Local, header.Name);
        }

        foreach (Binding binding in Bindings.Values)
        {
            yield return new SSKeybindSetting(
                BaseId + binding.Local,
                binding.Label,
                binding.DefaultKey,
                preventInteractionOnGui: binding.PreventInteractionOnGui,
                allowSpectatorTrigger: binding.AllowSpectatorTrigger,
                hint: binding.Hint);
        }

        foreach (ValueSetting setting in ValueSettings.Values)
        {
            yield return setting.Build(BaseId + setting.Local);
        }
    }

    internal IEnumerable<int> OwnedIds()
    {
        foreach (HeaderEntry header in Headers)
        {
            yield return BaseId + header.Local;
        }

        foreach (Binding binding in Bindings.Values)
        {
            yield return BaseId + binding.Local;
        }


        foreach (ValueSetting setting in ValueSettings.Values)
        {
            yield return BaseId + setting.Local;
        }
    }

    internal bool IsVisibleTo(Player player) => VisibilityFilter?.Invoke(player) != false;

    private bool IsLocalUsed(int local) =>
        Bindings.ContainsKey(local) || ValueSettings.ContainsKey(local) || Headers.Exists(h => h.Local == local);

    private void ValidateAvailableValueLocal(int local)
    {
        ValidateLocal(local);
        if (local == 0)
        {
            throw new ArgumentException("Local id 0 is reserved for the group header; use Header(name).", nameof(local));
        }

        if (IsLocalUsed(local))
        {
            throw new ArgumentException($"Local id {local} already used in block '{Owner}' ({BaseId}).", nameof(local));
        }
    }

    private static void ValidateLocal(int local)
    {
        if (local < 0 || local >= SssIdBlocks.BlockWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(local), local, $"Local id must be 0..{SssIdBlocks.BlockWidth - 1}.");
        }
    }

    internal readonly struct HeaderEntry
    {
        public HeaderEntry(int local, string name)
        {
            Local = local;
            Name = name;
        }

        public int Local { get; }

        public string Name { get; }
    }

    internal sealed class Binding
    {
        public Binding(int local, string label, KeyCode defaultKey, string hint, Action<Player> onPressed, Action<Player>? onReleased, bool preventInteractionOnGui, bool allowSpectatorTrigger)
        {
            Local = local;
            Label = label;
            DefaultKey = defaultKey;
            Hint = hint;
            OnPressed = onPressed;
            OnReleased = onReleased;
            PreventInteractionOnGui = preventInteractionOnGui;
            AllowSpectatorTrigger = allowSpectatorTrigger;
        }

        public int Local { get; }

        public string Label { get; }

        public KeyCode DefaultKey { get; }

        public string Hint { get; }

        public Action<Player> OnPressed { get; }

        public Action<Player>? OnReleased { get; }

        public bool PreventInteractionOnGui { get; }

        public bool AllowSpectatorTrigger { get; }
    }

    internal abstract class ValueSetting
    {
        protected ValueSetting(int local, string label, string hint)
        {
            Local = local;
            Label = label;
            Hint = hint;
        }

        public int Local { get; }

        public string Label { get; }

        protected string Hint { get; }

        public abstract ServerSpecificSettingBase Build(int absoluteId);

        public abstract void Invoke(Player player, ServerSpecificSettingBase setting);
    }

    private sealed class DropdownSetting : ValueSetting
    {
        private readonly string[] _options;
        private readonly int _defaultIndex;
        private readonly SSDropdownSetting.DropdownEntryType _entryType;
        private readonly Action<Player, int> _onChanged;

        public DropdownSetting(int local, string label, string[] options, int defaultIndex, SSDropdownSetting.DropdownEntryType entryType, string hint, Action<Player, int> onChanged)
            : base(local, label, hint)
        {
            _options = options;
            _defaultIndex = defaultIndex;
            _entryType = entryType;
            _onChanged = onChanged;
        }

        public override ServerSpecificSettingBase Build(int absoluteId) =>
            new SSDropdownSetting(absoluteId, Label, _options, _defaultIndex, _entryType, Hint);

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSDropdownSetting dropdown)
            {
                _onChanged(player, Mathf.Clamp(dropdown.SyncSelectionIndexValidated, 0, _options.Length - 1));
            }
        }
    }

    private sealed class SliderSetting : ValueSetting
    {
        private readonly float _min;
        private readonly float _max;
        private readonly float _defaultValue;
        private readonly bool _integer;
        private readonly string _valueFormat;
        private readonly string _displayFormat;
        private readonly Action<Player, float> _onChanged;

        public SliderSetting(int local, string label, float min, float max, float defaultValue, bool integer, string valueFormat, string displayFormat, string hint, Action<Player, float> onChanged)
            : base(local, label, hint)
        {
            _min = min;
            _max = max;
            _defaultValue = defaultValue;
            _integer = integer;
            _valueFormat = valueFormat;
            _displayFormat = displayFormat;
            _onChanged = onChanged;
        }

        public override ServerSpecificSettingBase Build(int absoluteId) =>
            new SSSliderSetting(absoluteId, Label, _min, _max, _defaultValue, _integer, _valueFormat, _displayFormat, Hint);

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSSliderSetting slider)
            {
                _onChanged(player, Mathf.Clamp(slider.SyncFloatValue, _min, _max));
            }
        }
    }
}
