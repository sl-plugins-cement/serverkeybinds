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
    var foreign = KeybindRegistry.ClaimBlock(24000, "conflicting consumer");
    foreign.Enable();
    bool collision = false;
    try { PluginMusicPreferences.Acquire(); } catch (InvalidOperationException) { collision = true; }
    Check(collision && ReferenceEquals(KeybindRegistry.Active, foreign), "registration failure preserves the existing block owner");
    foreign.Disable();
    var reinforcements = PluginMusicPreferences.Acquire();
    var block = KeybindRegistry.Active!;
    PlayerEvents.Disconnect(new Player(null!));
    Check(KeybindRegistry.Active == block, "unauthenticated disconnect does not break shared settings");
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

    using (PluginMusicPreferences.Acquire())
    {
        var volumeBlock = KeybindRegistry.Active!;
        Check(volumeBlock.SliderMin == 0f && volumeBlock.SliderMax == 100f, "volume slider spans 0-100 percent");
        Check(PluginMusicPreferences.VolumePercentOf(bob) == 100 && PluginMusicPreferences.VolumeOf(bob) == 1f, "players start at full volume");
        int notified = -1;
        Action<Player, int> onChanged = (player, percent) => { if (player == bob) notified = percent; };
        PluginMusicPreferences.VolumePercentChanged += onChanged;
        volumeBlock.ReceiveVolume(bob, 37f);
        Check(PluginMusicPreferences.VolumePercentOf(bob) == 37 && Math.Abs(PluginMusicPreferences.VolumeOf(bob) - 0.37f) < 0.001f && notified == 37, "slider choice applies immediately and notifies consumers");
        notified = -1;
        volumeBlock.ReceiveVolume(bob, 37f);
        Check(notified == -1, "duplicate volume response does not re-notify");
        volumeBlock.ReceiveVolume(bob, 250f);
        Check(PluginMusicPreferences.VolumePercentOf(bob) == 100, "out-of-range slider values clamp");
        volumeBlock.ReceiveVolume(bob, 0f);
        Check(PluginMusicPreferences.VolumeOf(bob) == 0f, "zero percent is a valid silent choice");
        PluginMusicPreferences.SetVolumePercent(bob, 100);
        Check(PluginMusicPreferences.VolumeOf(bob) == 1f && notified == 100, "console reset returns to full volume");
        PluginMusicPreferences.SetVolumePercent(bob, 25);
        PluginMusicPreferences.VolumePercentChanged -= onChanged;
    }
    using (PluginMusicPreferences.Acquire())
        Check(PluginMusicPreferences.VolumeOf(bob) == 0.25f, "volume percent survives full service reload");

    string storePath = Path.Combine(root, "isolated", "muted.txt");
    string volumePath = Path.Combine(root, "isolated", "volume.txt");
    var store = new MusicPreferenceStore(storePath, volumePath);
    store.SetMuted("alice@steam", true);
    store.SetMuted("bob@steam", true);
    store.SetMuted("alice@steam", false);
    store.SetVolumePercent("alice@steam", 30);
    store.SetVolumePercent("bob@steam", 10);
    store.SetVolumePercent("bob@steam", 100);
    var restored = new MusicPreferenceStore(storePath, volumePath);
    Check(!restored.IsMuted("alice@steam") && restored.IsMuted("bob@steam"), "atomic replacement persists mute and unmute independently");
    Check(restored.VolumePercent("alice@steam") == 30 && restored.VolumePercent("bob@steam") == 100, "volume percents persist and the default is dropped from disk");
    bool tabRejected = false;
    try { store.SetVolumePercent("tab	user", 1); } catch (ArgumentException) { tabRejected = true; }
    Check(tabRejected, "a tab in the identity cannot corrupt the volume record format");
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
