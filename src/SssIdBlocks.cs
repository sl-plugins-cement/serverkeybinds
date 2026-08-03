namespace ServerKeybinds;

/// <summary>
/// The single source of truth for Server-Specific-Settings id allocation across the whole server.
/// Every plugin owns one fixed, 1000-wide block: id = <c>base + local</c>, where local 0 is the
/// group header and 1..999 are settings. Because <see cref="UserSettings.ServerSpecific.ServerSpecificSettingsSync.DefinedSettings"/>
/// is a single static array shared by EVERY plugin, blocks must never overlap; <see cref="KeybindRegistry.ClaimBlock"/>
/// enforces that at load time. Add a new plugin here rather than picking a bare id inline.
/// </summary>
public static class SssIdBlocks
{
    /// <summary>Width of every plugin's reserved id block. Bases must be a multiple of this.</summary>
    public const int BlockWidth = 1000;

    // --- Custom-item / ability plugins (7-digit convention already in use) ---

    /// <summary>"SCP Enhacements" / scp-106 wiki abilities (was 1060100-1060106).</summary>
    public const int Scp106 = 1060000;

    /// <summary>reinforcements-system: SRA deploy (was 1090100/01). The plugin owns this block + <see cref="ReinforcementsMedic"/>.</summary>
    public const int Reinforcements = 1090000;

    /// <summary>reinforcements-system Medic field-heal (was 1090200/01). A separate block so the SRA and Medic services stay independent.</summary>
    public const int ReinforcementsMedic = 1091000;

    /// <summary>reinforcements-system Serpent's Hand abilities (Mouse0/Mouse1). Its own block so Serpent abilities work even when SRA/Medic are disabled (a 1000-aligned base can be Enable()d by only one KeybindBlock).</summary>
    public const int SerpentsHand = 1092000;

    /// <summary>goc-nuke: reserved for future ability keybinds (none today).</summary>
    public const int GocNuke = 1100000;

    // --- Config-driven toggle plugins (documented here so new blocks never land on them) ---

    /// <summary>global-music-player mute toggle (currently 24000/24001).</summary>
    public const int GlobalMusic = 24000;

    /// <summary>MvpSystem music toggle (currently the bare id 300; re-home here when migrated).</summary>
    public const int MvpSystem = 25000;

    /// <summary>EffectDisplay time-effect toggle (currently 2030/2031; re-home here when migrated).</summary>
    public const int EffectDisplay = 26000;

    /// <summary>CustomizableUIMeow HUD toggles (already based at 530210; the block covers its full span).</summary>
    public const int CustomizableUi = 530000;

    /// <summary>True if <paramref name="settingId"/> falls inside the 1000-wide block at <paramref name="baseId"/>.</summary>
    public static bool Contains(int baseId, int settingId) =>
        settingId >= baseId && settingId < baseId + BlockWidth;

    /// <summary>The 1000-aligned block base that would own <paramref name="settingId"/>.</summary>
    public static int BaseOf(int settingId) => settingId - Mod(settingId, BlockWidth);

    private static int Mod(int value, int modulus)
    {
        int r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
