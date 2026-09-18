// Only the native boundary is stubbed; preference lifecycle and disk persistence are production code.
namespace LabApi.Features.Wrappers
{
    public sealed class Player(string userId) { public string UserId { get; } = userId; }
    public static class Server { public static ushort Port => 7777; }
}
namespace LabApi.Loader.Features.Paths
{
    public static class PathManager { public static DirectoryInfo Configs { get; set; } = null!; }
}
namespace LabApi.Events.Arguments.PlayerEvents
{
    public sealed class PlayerLeftEventArgs(LabApi.Features.Wrappers.Player player)
    { public LabApi.Features.Wrappers.Player Player { get; } = player; }
}
namespace LabApi.Events.Handlers
{
    public static class PlayerEvents
    {
        public static event Action<LabApi.Events.Arguments.PlayerEvents.PlayerLeftEventArgs>? Left;
        public static void Disconnect(LabApi.Features.Wrappers.Player player) => Left?.Invoke(new(player));
    }
}
namespace ServerKeybinds
{
    using LabApi.Features.Wrappers;
    public enum SettingsCategory { Announcements }
    public static class SssIdBlocks { public const int GlobalMusic = 24000; }
    public static class KeybindRegistry
    {
        public static string Language { get; set; } = "en";
        public static KeybindBlock? Active;
        public static KeybindBlock ClaimBlock(int id, string owner) => new();
    }
    public sealed class KeybindBlock
    {
        public bool Active;
        public Action<Player, bool> Receive = null!;
        public Action<Player, float> ReceiveVolume = null!;
        public Func<Player, bool> Default = null!;
        public float SliderMin, SliderMax;
        public KeybindBlock InCategory(SettingsCategory category) => this;
        public KeybindBlock Header(string label) => this;
        public KeybindBlock AddTwoButtons(int id, string label, string a, string b,
            Func<Player, bool> initial, bool fallback, string hint, Action<Player, bool> changed)
        { Default = initial; Receive = changed; return this; }
        public KeybindBlock AddSlider(int id, string label, float min, float max, float initial, bool integer, string valueFormat, string displayFormat, string hint, Action<Player, float> changed)
        { SliderMin = min; SliderMax = max; ReceiveVolume = changed; return this; }
        public void Enable()
        {
            if (KeybindRegistry.Active != null) throw new InvalidOperationException("Duplicate setting");
            KeybindRegistry.Active = this;
            Active = true;
        }
        public void Disable() { if (KeybindRegistry.Active == this) KeybindRegistry.Active = null; Active = false; }
    }
}
