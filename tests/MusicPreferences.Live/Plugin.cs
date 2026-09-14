using System;
using System.Linq;
using CommandSystem;
using LabApi.Features.Wrappers;
using Mirror;
using NetworkManagerUtils.Dummies;
using RemoteAdmin;
using UserSettings.ServerSpecific;

namespace ServerKeybinds.MusicPreferencesLive;

public sealed class Plugin : LabApi.Loader.Features.Plugins.Plugin
{
    public override string Name => "Music preference integration probe";
    public override string Description => "Local-only shared music contract verification through native speaker predicates.";
    public override string Author => "Local tests";
    public override Version Version => new(1, 0, 0);
    public override Version RequiredApiVersion => new(1, 1, 5);
    public override void Enable() { }
    public override void Disable() => Probe.Cleanup();
}

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class Probe : ICommand
{
    private static Player? _muted;
    private static Player? _listening;
    public string Command => "musicverify";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Local server-console-only music preference integration probe.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (sender != ServerConsole.Scs) { response = "Local server console only."; return false; }
        try
        {
            string action = arguments.Count == 0 ? "check" : arguments.At(0);
            if (action == "prepare")
            {
                Cleanup();
                _muted = Spawn("music-test-muted");
                _listening = Spawn("music-test-listening");
                PluginMusicPreferences.SetMuted(_muted, true);
                PluginMusicPreferences.SetMuted(_listening, false);
                response = "Prepared two listeners with independent shared music preferences.";
                return true;
            }
            if (action == "cleanup") { Cleanup(); response = "Cleaned up probe dummies and opt-outs."; return true; }
            Require(_muted != null && _listening != null, "prepare first");
            if (action == "play")
            {
                Require(arguments.Count == 2, "play requires a local audio file path");
                // Dummy authentication assigns ID_Dummy on its first update, after SpawnDummy returns.
                // Reapply distinct test identities after that initialization has completed.
                _muted!.ReferenceHub.authManager.UserId = "music-test-muted@local-test";
                _listening!.ReferenceHub.authManager.UserId = "music-test-listening@local-test";
                PluginMusicPreferences.SetMuted(_muted, true);
                PluginMusicPreferences.SetMuted(_listening, false);
                _listening!.UserGroup = new UserGroup { Permissions = (ulong)PlayerPermissions.Broadcasting, BadgeText = "Music test", BadgeColor = "silver" };
                // A local dummy has no RA authentication handshake. Enable its native RA access for this test sender.
                _listening.ReferenceHub.serverRoles.RemoteAdmin = true;
                _listening.ReferenceHub.serverRoles.Permissions = (ulong)PlayerPermissions.Broadcasting;
                var playerSender = new PlayerCommandSender(_listening.ReferenceHub);
                Require(Player.Get(playerSender) == _listening, "native sender resolved to a different player");
                Require(_listening.RemoteAdminAccess && _listening.HasPermission(PlayerPermissions.Broadcasting), "test admin permissions were not applied");
                response = Server.RunCommand("/gmp play " + arguments.At(1) + " --no-message", playerSender);
                return true;
            }
            Require(ServerSpecificSettingsSync.DefinedSettings.Count(x => x.SettingId == PluginMusicPreferences.SettingId) == 1,
                "exactly one shared setting must be registered");
            SpeakerToy[] speakers = SpeakerToy.List.Where(x => !x.IsSpatial && x.ValidPlayers != null).ToArray();
            Require(speakers.Length >= 3, "expected at least three active music speakers, found " + speakers.Length);
            foreach (SpeakerToy speaker in speakers)
            {
                Require(!speaker.ValidPlayers!(_muted!), "muted listener receives controller " + speaker.ControllerId);
                Require(speaker.ValidPlayers!(_listening!), "unmuted listener excluded from global controller " + speaker.ControllerId);
            }
            PluginMusicPreferences.SetMuted(_muted!, false);
            foreach (SpeakerToy speaker in speakers)
                Require(speaker.ValidPlayers!(_muted!), "unmute did not update existing controller " + speaker.ControllerId);
            PluginMusicPreferences.SetMuted(_muted!, true);
            foreach (SpeakerToy speaker in speakers)
                Require(!speaker.ValidPlayers!(_muted!), "remute did not update existing controller " + speaker.ControllerId);
            response = "PASS one setting; three playback integrations; isolated mute/unmute/remute on existing controllers " +
                string.Join(",", speakers.Select(x => x.ControllerId));
            return true;
        }
        catch (Exception ex) { response = "FAIL " + ex; return false; }
    }

    private static Player Spawn(string name)
    {
        ReferenceHub hub = DummyUtils.SpawnDummy(name);
        hub.authManager.UserId = name + "@local-test";
        return Player.Get(hub);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void Cleanup()
    {
        foreach (Player? player in new[] { _muted, _listening })
        {
            if (player == null || player.IsDestroyed) continue;
            PluginMusicPreferences.SetMuted(player, false);
            NetworkServer.Destroy(player.GameObject);
        }
        _muted = _listening = null;
    }
}
