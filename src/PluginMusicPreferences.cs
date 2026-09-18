using System;
using System.Collections.Generic;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;

namespace ServerKeybinds;

/// <summary>
/// Shared music preferences: an opt-out toggle and a per-player volume percent. Acquire one lease per
/// enabled consumer and dispose it after stopping audio. Call lifecycle and setting mutations on the
/// game thread. Recipient queries are safe on audio threads. This service owns preferences only; each
/// plugin still owns its playback and timeline.
///
/// A speaker's network volume is one value every listener shares, so per-listener volume is applied
/// server-side: scale the PCM frame by <see cref="VolumeOf"/> before Opus-encoding, grouping listeners
/// with the same percent so each distinct value costs one encode, and send each group its own packet
/// on the same controller id. A consumer that keeps the stock single-stream transmitter plays at full
/// volume for everyone; the mute still applies through <see cref="CanReceiveMusic"/>.
/// </summary>
public static class PluginMusicPreferences
{
    public const int SettingId = SssIdBlocks.GlobalMusic + 1;
    public const int VolumeSettingId = SssIdBlocks.GlobalMusic + 2;
    public const int DefaultVolumePercent = MusicPreferenceStore.DefaultVolumePercent;

    private static MusicPreferenceStore? _store;
    private static KeybindBlock? _block;
    private static int _consumers;
    private static readonly Dictionary<string, bool> ClientMuteChoices = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ClientVolumeChoices = new(StringComparer.Ordinal);

    /// <summary>Raised on the game thread after a player's volume percent changed, with the new value.</summary>
    public static event Action<Player, int>? VolumePercentChanged;

    public static IDisposable Acquire()
    {
        if (_consumers == 0)
        {
            string directory = Path.Combine(PathManager.Configs.FullName, Server.Port.ToString(), "ServerKeybinds");
            _store = new MusicPreferenceStore(
                Path.Combine(directory, "plugin_music_muted.txt"),
                Path.Combine(directory, "plugin_music_volume.txt"));
            bool english = string.Equals(KeybindRegistry.Language, "en", StringComparison.OrdinalIgnoreCase);
            var block = KeybindRegistry.ClaimBlock(SssIdBlocks.GlobalMusic, "Plugin Music")
                .InCategory(SettingsCategory.Announcements)
                .Header(english ? "Plugin music" : "插件音乐")
                .AddTwoButtons(1, english ? "Plugin music" : "插件音乐",
                    english ? "On" : "开启", english ? "Muted" : "静音",
                    IsMuted, false,
                    english ? "Mute music from participating plugins for yourself. Gameplay sounds are unaffected."
                        : "仅为自己静音已接入插件的音乐，不影响游戏音效。",
                    OnClientMuteChoice)
                .AddSlider(2, english ? "Plugin music volume" : "插件音乐音量",
                    0f, 100f, DefaultVolumePercent, true, "0", "{0}%",
                    english ? "Volume of music from participating plugins, for you only."
                        : "仅为自己调整已接入插件的音乐音量。",
                    OnClientVolumeChoice);
            try { block.Enable(); }
            catch { if (block.Active) block.Disable(); throw; }
            _block = block;
            PlayerEvents.Left += OnLeft;
        }
        _consumers++;
        return new Lease();
    }

    public static bool IsMuted(Player player) => player != null && (_store?.IsMuted(player.UserId) ?? false);

    /// <summary>Compose this with any existing audience predicate, never replace audience restrictions.</summary>
    public static bool CanReceiveMusic(Player player) => player != null && !IsMuted(player);

    /// <summary>The player's chosen volume, 0-100; 100 until they move the slider.</summary>
    public static int VolumePercentOf(Player player) => player == null ? DefaultVolumePercent : (_store?.VolumePercent(player.UserId) ?? DefaultVolumePercent);

    /// <summary>The player's volume multiplier, 0-1. Mute is separate: check <see cref="CanReceiveMusic"/> first.</summary>
    public static float VolumeOf(Player player) => VolumePercentOf(player) / 100f;

    /// <summary>
    /// Persists a console/API choice. Native client-owned toggles cannot be set by the server;
    /// the next changed menu value, or the client's saved value on reconnect, takes precedence.
    /// </summary>
    public static void SetMuted(Player player, bool muted)
    {
        if (_consumers == 0 || _store == null) throw new InvalidOperationException("No music preference consumer is enabled.");
        _store.SetMuted(player.UserId, muted);
    }

    /// <summary>Persists a volume percent (0-100) and notifies consumers when it changed.</summary>
    public static void SetVolumePercent(Player player, int percent)
    {
        if (_consumers == 0 || _store == null) throw new InvalidOperationException("No music preference consumer is enabled.");
        if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        int previous = _store.VolumePercent(player.UserId);
        _store.SetVolumePercent(player.UserId, percent);
        if (previous != percent) VolumePercentChanged?.Invoke(player, percent);
    }

    private static void OnClientMuteChoice(Player player, bool muted)
    {
        if (ClientMuteChoices.TryGetValue(player.UserId, out bool previous) && previous == muted) return;
        SetMuted(player, muted);
        ClientMuteChoices[player.UserId] = muted;
    }

    private static void OnClientVolumeChoice(Player player, float value)
    {
        if (float.IsNaN(value)) return;
        int percent = Math.Max(0, Math.Min(100, (int)Math.Round(value)));
        if (ClientVolumeChoices.TryGetValue(player.UserId, out int previous) && previous == percent) return;
        SetVolumePercent(player, percent);
        ClientVolumeChoices[player.UserId] = percent;
    }

    private static void OnLeft(PlayerLeftEventArgs ev)
    {
        string? userId = ev.Player?.UserId;
        if (userId == null) return;
        ClientMuteChoices.Remove(userId);
        ClientVolumeChoices.Remove(userId);
    }

    private sealed class Lease : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--_consumers != 0) return;
            _block?.Disable();
            _block = null;
            PlayerEvents.Left -= OnLeft;
            ClientMuteChoices.Clear();
            ClientVolumeChoices.Clear();
        }
    }
}
