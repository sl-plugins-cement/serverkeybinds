using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;
using ServerKeybinds;

string root = Path.Combine(Path.GetTempPath(), "plugin-music-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
PathManager.Configs = new DirectoryInfo(root);
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
try
{
    var alice = new Player("alice@steam");
    var bob = new Player("bob@steam");
    var reinforcements = PluginMusicPreferences.Acquire();
    var block = KeybindRegistry.Active!;
    var omega = PluginMusicPreferences.Acquire();
    Check(ReferenceEquals(block, KeybindRegistry.Active), "multiple consumers register exactly one setting without GMP");
    Check(!block.Default(alice) && PluginMusicPreferences.CanReceiveMusic(bob), "new players hear music by default");
    Func<Player, bool> runningMusic = PluginMusicPreferences.CanReceiveMusic;
    block.Receive(alice, true);
    Check(!runningMusic(alice) && runningMusic(bob), "current playback reacts immediately and isolates listeners");
    Func<Player, bool> scopedMusic = player => player == alice && runningMusic(player);
    Check(!scopedMusic(bob) && !scopedMusic(alice), "mute composes with an existing audience restriction");
    PluginMusicPreferences.SetMuted(alice, false);
    block.Receive(alice, true);
    Check(runningMusic(alice), "duplicate client response does not undo a console choice");
    block.Receive(alice, false);
    block.Receive(alice, true);
    Check(!runningMusic(alice), "explicit menu changes take precedence over console choice");
    reinforcements.Dispose();
    reinforcements.Dispose();
    Check(ReferenceEquals(block, KeybindRegistry.Active), "idempotent consumer unload leaves other consumers registered");
    PlayerEvents.Disconnect(alice);
    var reconnected = new Player("alice@steam");
    Check(!runningMusic(reconnected), "stable user ID restores mute before client settings arrive");
    block.Receive(reconnected, false);
    Check(runningMusic(reconnected), "reconnect accepts the client's saved choice");
    PluginMusicPreferences.SetMuted(reconnected, true);
    omega.Dispose();
    Check(KeybindRegistry.Active == null, "last consumer releases the setting");
    using (PluginMusicPreferences.Acquire())
        Check(KeybindRegistry.Active!.Default(reconnected) && !runningMusic(reconnected), "disk persistence survives full service reload");

    string storePath = Path.Combine(root, "isolated", "muted.txt");
    var store = new MusicPreferenceStore(storePath);
    store.SetMuted("alice@steam", true);
    store.SetMuted("bob@steam", true);
    store.SetMuted("alice@steam", false);
    var restored = new MusicPreferenceStore(storePath);
    Check(!restored.IsMuted("alice@steam") && restored.IsMuted("bob@steam"), "atomic replacement persists mute and unmute independently");
    bool rejected = false;
    try { store.SetMuted("injected\nuser", true); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "invalid identity cannot inject a stored record");
    using (File.Open(storePath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        bool failed = false;
        try { store.SetMuted("charlie@steam", true); } catch (IOException) { failed = true; }
        Check(failed && !store.IsMuted("charlie@steam"), "failed persistence does not claim a saved choice");
    }
    Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic writes clean up temporary files");
    Console.WriteLine($"PASS {checks} checks");
}
finally { Directory.Delete(root, true); }
