using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;

namespace ServerKeybinds;

// TEMPORARY (15.0 beta): the registry side of ConsoleBindFallback. Remove this file with it once the
// 15.0 client ships its Server-Specific Settings tab.
public static partial class KeybindRegistry
{
    /// <summary>One active keybind as the console-bind fallback sees it.</summary>
    internal readonly struct ConsoleKeybind
    {
        public ConsoleKeybind(int settingId, KeybindBlock block, KeybindBlock.Binding binding)
        {
            SettingId = settingId;
            Block = block;
            Binding = binding;
        }

        public int SettingId { get; }

        public KeybindBlock Block { get; }

        public KeybindBlock.Binding Binding { get; }
    }

    /// <summary>
    /// The keybinds <paramref name="player"/> would see in the settings tab right now, in menu order:
    /// active, not yielded to a foreign entry, and in a block visible to them.
    /// </summary>
    internal static List<ConsoleKeybind> ConsoleKeybindsFor(Player player) =>
        ActiveBindings
            .Where(pair => pair.Value.Block.IsVisibleTo(player))
            .OrderBy(pair => (int)pair.Value.Block.Category)
            .ThenBy(pair => pair.Value.Block.SortOrder)
            .ThenBy(pair => pair.Value.Block.BaseId)
            .ThenBy(pair => pair.Key)
            .Select(pair => new ConsoleKeybind(pair.Key, pair.Value.Block, pair.Value.Binding))
            .ToList();

    /// <summary>True while <paramref name="player"/> holds a latched press of <paramref name="settingId"/>.</summary>
    internal static bool IsLatched(Player player, int settingId) =>
        !string.IsNullOrWhiteSpace(player.UserId) &&
        Pressed.TryGetValue(player.UserId, out HashSet<int> pressed) && pressed.Contains(settingId);

    /// <summary>
    /// Feeds one keybind edge through the same latch and dispatch as a settings-tab response, so consumers
    /// cannot tell the two apart. It does not count as a delivery acknowledgement: a console command proves
    /// nothing about the client having processed the settings pack. Returns true when an edge was dispatched.
    /// </summary>
    internal static bool InjectKeyEdge(Player player, int settingId, bool isPressed)
    {
        ReferenceHub? hub = player.ReferenceHub;
        if (hub == null || string.IsNullOrWhiteSpace(player.UserId) ||
            !ActiveBindings.TryGetValue(settingId, out ActiveBinding activeBinding))
        {
            return false;
        }

        // A press needs the block visible, like a wire press. A release does not: it settles a press that
        // was accepted earlier, matching the registry's own forced releases.
        if (isPressed && !activeBinding.Block.IsVisibleTo(player))
        {
            return false;
        }

        if (PressTrace)
        {
            LabApi.Features.Console.Logger.Debug(
                $"[ServerKeybinds] Trace: console {(isPressed ? "press" : "release")} for id {settingId} (player {hub.PlayerId}).",
                PressTrace);
        }

        return ApplyKeyEdge(hub, player.UserId, settingId, activeBinding, isPressed);
    }
}
