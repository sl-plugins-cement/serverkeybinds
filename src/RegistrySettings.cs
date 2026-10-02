using System;
using System.IO;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;

namespace ServerKeybinds;

/// <summary>
/// The per-port registry settings file, <c>LabAPI/configs/&lt;port&gt;/ServerKeybinds/config.yml</c>.
/// ServerKeybinds is a library rather than a LabAPI plugin, so it reads and writes this one-key file
/// itself. A missing or unreadable file means the default, <see cref="ForeignSettingsPolicy.Merge"/>.
/// </summary>
internal static class RegistrySettings
{
    private const string ForeignSettingsKey = "foreign_settings";

    public static string FilePath =>
        Path.Combine(PathManager.Configs.FullName, Server.Port.ToString(), "ServerKeybinds", "config.yml");

    public static ForeignSettingsPolicy LoadForeignPolicy()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return ForeignSettingsPolicy.Merge;
            }

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(ForeignSettingsKey + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = line.Substring(ForeignSettingsKey.Length + 1).Trim().Trim('"', '\'');
                if (Enum.TryParse(value, true, out ForeignSettingsPolicy policy)
                    && Enum.IsDefined(typeof(ForeignSettingsPolicy), policy))
                {
                    return policy;
                }

                Logger.Warn($"[ServerKeybinds] {path}: unknown {ForeignSettingsKey} '{value}'; using Merge.");
                return ForeignSettingsPolicy.Merge;
            }

            return ForeignSettingsPolicy.Merge;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] Could not read {path}; foreign settings stay Merge: {exception.GetBaseException().Message}");
            return ForeignSettingsPolicy.Merge;
        }
    }

    public static bool TrySaveForeignPolicy(ForeignSettingsPolicy policy, out string path)
    {
        path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "# Merge (default) keeps other plugins' Server-Specific Settings beside the registry's.\n" +
                "# Block strips them, so only registered blocks reach clients.\n" +
                $"{ForeignSettingsKey}: {policy}\n");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] Could not save {path}: {exception.GetBaseException().Message}");
            return false;
        }
    }
}
