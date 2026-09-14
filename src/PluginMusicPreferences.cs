using System;
using System.Collections.Generic;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;

namespace ServerKeybinds;

/// <summary>
/// Shared music opt-out. Acquire one lease per enabled consumer and dispose it after stopping audio.
/// Call lifecycle and setting mutations on the game thread. Recipient queries are safe on audio threads.
/// This service owns preferences only; each plugin still owns its playback and timeline.
/// </summary>
public static class PluginMusicPreferences
{
    public const int SettingId = SssIdBlocks.GlobalMusic + 1;
    private static MusicPreferenceStore? _store;
    private static KeybindBlock? _block;
    private static int _consumers;
    private static readonly Dictionary<string, bool> ClientChoices = new(StringComparer.Ordinal);

    public static IDisposable Acquire()
    {
        if (_consumers == 0)
        {
            _store = new MusicPreferenceStore(Path.Combine(PathManager.Configs.FullName,
                Server.Port.ToString(), "ServerKeybinds", "plugin_music_muted.txt"));
            bool english = string.Equals(KeybindRegistry.Language, "en", StringComparison.OrdinalIgnoreCase);
            var block = KeybindRegistry.ClaimBlock(SssIdBlocks.GlobalMusic, "Plugin Music")
                .InCategory(SettingsCategory.Announcements)
                .Header(english ? "Plugin music" : "插件音乐")
                .AddTwoButtons(1, english ? "Plugin music" : "插件音乐",
                    english ? "On" : "开启", english ? "Muted" : "静音",
                    IsMuted, false,
                    english ? "Mute music from participating plugins for yourself. Gameplay sounds are unaffected."
                        : "仅为自己静音已接入插件的音乐，不影响游戏音效。",
                    OnClientChoice);
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

    /// <summary>
    /// Persists a console/API choice. Native client-owned toggles cannot be set by the server;
    /// the next changed menu value, or the client's saved value on reconnect, takes precedence.
    /// </summary>
    public static void SetMuted(Player player, bool muted)
    {
        if (_consumers == 0 || _store == null) throw new InvalidOperationException("No music preference consumer is enabled.");
        _store.SetMuted(player.UserId, muted);
    }

    private static void OnClientChoice(Player player, bool muted)
    {
        if (ClientChoices.TryGetValue(player.UserId, out bool previous) && previous == muted) return;
        SetMuted(player, muted);
        ClientChoices[player.UserId] = muted;
    }

    private static void OnLeft(PlayerLeftEventArgs ev)
    {
        string? userId = ev.Player?.UserId;
        if (!string.IsNullOrEmpty(userId)) ClientChoices.Remove(userId);
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
            ClientChoices.Clear();
        }
    }
}
