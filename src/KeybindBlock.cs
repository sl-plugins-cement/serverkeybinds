using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;
using UserSettings.ServerSpecific;

namespace ServerKeybinds;

/// <summary>
/// One plugin's claimed 1000-wide id block. Build it fluently with <see cref="Header"/> + <see cref="Add"/>,
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
    internal bool Active;

    internal KeybindBlock(int baseId, string owner)
    {
        BaseId = baseId;
        Owner = owner;
    }

    /// <summary>Adds the block's group header at local id 0 (shown above its settings in the SSS menu).</summary>
    public KeybindBlock Header(string groupName) => Header(0, groupName);

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

        if (Bindings.ContainsKey(local) || Headers.Exists(h => h.Local == local))
        {
            throw new ArgumentException($"Local id {local} already used in block '{Owner}' ({BaseId}).", nameof(local));
        }

        Bindings[local] = new Binding(local, label, defaultKey, hint, onPressed, onReleased, preventInteractionOnGui, allowSpectatorTrigger);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>The absolute SSS id for a local id (base + local) — e.g. for sibling services rendering key glyphs.</summary>
    public int SettingId(int local)
    {
        ValidateLocal(local);
        return BaseId + local;
    }

    /// <summary>Merges this block's header(s) + settings into the shared <c>DefinedSettings</c> and broadcasts.</summary>
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
}
