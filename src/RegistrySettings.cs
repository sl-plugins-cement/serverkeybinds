using System;
using System.IO;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Paths;

namespace ServerKeybinds;

/// <summary>
/// The per-port registry settings file, <c>LabAPI/configs/&lt;port&gt;/ServerKeybinds/config.yml</c>.
/// ServerKeybinds is a library rather than a LabAPI plugin, so it reads and writes this small flat file
/// itself. A missing or unreadable file means the defaults: <see cref="ForeignSettingsPolicy.Merge"/>, and
/// the temporary console-bind fallback on.
/// </summary>
internal static class RegistrySettings
{
    private const string ForeignSettingsKey = "foreign_settings";

    // TEMPORARY (15.0 beta): remove with ConsoleBindFallback once the 15.0 client ships its
    // server-specific settings tab.
    private const string ConsoleBindKey = "console_bind_fallback";
    private const string ConsoleBindComment =
        "# TEMPORARY for the 15.0 beta client, which has no Server-Specific Settings tab: players bind\n" +
        "# keybinds through the native console (.binds / .kb). true (default) or false.\n";

    /// <summary>Whether the temporary console-bind fallback (<c>.kb</c>, <c>.binds</c>, console reminder) is on.</summary>
    public static bool LoadConsoleBindFallback()
    {
        string? value = ReadValue(ConsoleBindKey);
        if (value == null)
        {
            return true;
        }

        if (bool.TryParse(value, out bool enabled))
        {
            return enabled;
        }

        Logger.Warn($"[ServerKeybinds] {FilePath}: unknown {ConsoleBindKey} '{value}'; using true.");
        return true;
    }

    private static string? ReadValue(string key)
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    return line.Substring(key.Length + 1).Trim().Trim('"', '\'');
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] Could not read {path}; {key} keeps its default: {exception.GetBaseException().Message}");
        }

        return null;
    }

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
            // The file is rewritten whole, so carry the console-bind switch over.
            string? consoleBind = ReadValue(ConsoleBindKey);
            File.WriteAllText(path,
                "# Merge (default) keeps other plugins' Server-Specific Settings beside the registry's.\n" +
                "# Block strips them, so only registered blocks reach clients.\n" +
                $"{ForeignSettingsKey}: {policy}\n" +
                (consoleBind == null ? string.Empty : ConsoleBindComment + $"{ConsoleBindKey}: {consoleBind}\n"));
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[ServerKeybinds] Could not save {path}: {exception.GetBaseException().Message}");
            return false;
        }
    }
}
