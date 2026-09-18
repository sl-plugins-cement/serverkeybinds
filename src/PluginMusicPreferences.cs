using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;

namespace ServerKeybinds;

/// <summary>
/// Shared music preferences: an opt-out toggle and a per-player volume step. Acquire one lease per
/// enabled consumer and dispose it after stopping audio. Call lifecycle and setting mutations on the
/// game thread. Recipient queries are safe on audio threads. This service owns preferences only; each
/// plugin still owns its playback and timeline.
///
/// A speaker's volume is one network value shared by every listener, so per-player volume is delivered
/// by running one speaker per step and routing each listener to the speaker of their step:
/// <c>speaker[step].ValidPlayers = p =&gt; Audience(p) &amp;&amp; CanReceiveMusic(p) &amp;&amp; VolumeStepOf(p) == step</c>.
/// Consumers that keep a single speaker simply ignore the step and play at full volume.
/// </summary>
public static class PluginMusicPreferences
{
    public const int SettingId = SssIdBlocks.GlobalMusic + 1;
    public const int VolumeSettingId = SssIdBlocks.GlobalMusic + 2;

    /// <summary>Volume multipliers by step; step 0 is the default. Consumers spawn one speaker per entry.</summary>
    public static readonly IReadOnlyList<float> VolumeSteps = new[] { 1f, 0.75f, 0.5f, 0.25f };

    private static MusicPreferenceStore? _store;
    private static KeybindBlock? _block;
    private static int _consumers;
    private static readonly Dictionary<string, bool> ClientMuteChoices = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ClientVolumeChoices = new(StringComparer.Ordinal);

    /// <summary>Raised on the game thread after a player's volume step changed, with the new step.</summary>
    public static event Action<Player, int>? VolumeStepChanged;

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
                .AddDropdown(2, english ? "Plugin music volume" : "插件音乐音量",
                    VolumeSteps.Select(step => $"{step * 100f:0}%").ToArray(), 0,
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

    /// <summary>The player's chosen index into <see cref="VolumeSteps"/>; 0 until they pick something else.</summary>
    public static int VolumeStepOf(Player player)
    {
        int step = player == null ? 0 : (_store?.VolumeStep(player.UserId) ?? 0);
        return step >= 0 && step < VolumeSteps.Count ? step : 0;
    }

    /// <summary>The player's volume multiplier. Mute is separate: check <see cref="CanReceiveMusic"/> first.</summary>
    public static float VolumeOf(Player player) => VolumeSteps[VolumeStepOf(player)];

    /// <summary>
    /// Persists a console/API choice. Native client-owned toggles cannot be set by the server;
    /// the next changed menu value, or the client's saved value on reconnect, takes precedence.
    /// </summary>
    public static void SetMuted(Player player, bool muted)
    {
        if (_consumers == 0 || _store == null) throw new InvalidOperationException("No music preference consumer is enabled.");
        _store.SetMuted(player.UserId, muted);
    }

    /// <summary>Persists a volume step (an index into <see cref="VolumeSteps"/>) and notifies consumers.</summary>
    public static void SetVolumeStep(Player player, int step)
    {
        if (_consumers == 0 || _store == null) throw new InvalidOperationException("No music preference consumer is enabled.");
        if (step < 0 || step >= VolumeSteps.Count) throw new ArgumentOutOfRangeException(nameof(step));
        int previous = _store.VolumeStep(player.UserId);
        _store.SetVolumeStep(player.UserId, step);
        if (previous != step) VolumeStepChanged?.Invoke(player, step);
    }

    private static void OnClientMuteChoice(Player player, bool muted)
    {
        if (ClientMuteChoices.TryGetValue(player.UserId, out bool previous) && previous == muted) return;
        SetMuted(player, muted);
        ClientMuteChoices[player.UserId] = muted;
    }

    private static void OnClientVolumeChoice(Player player, int step)
    {
        if (step < 0 || step >= VolumeSteps.Count) return;
        if (ClientVolumeChoices.TryGetValue(player.UserId, out int previous) && previous == step) return;
        SetVolumeStep(player, step);
        ClientVolumeChoices[player.UserId] = step;
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
