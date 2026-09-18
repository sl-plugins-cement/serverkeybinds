using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerKeybinds;

/// <summary>
/// Port-local durable music preferences: an opt-out set and a per-player volume step. Reads never
/// touch the disk; every write replaces the whole file atomically and only updates memory on success.
/// </summary>
internal sealed class MusicPreferenceStore
{
    private readonly string _mutedPath;
    private readonly string _volumePath;
    private readonly object _gate = new();
    private readonly HashSet<string> _muted;
    private readonly Dictionary<string, int> _volumeSteps;

    public MusicPreferenceStore(string mutedPath, string volumePath)
    {
        _mutedPath = mutedPath;
        _volumePath = volumePath;
        _muted = new HashSet<string>(ReadLines(mutedPath).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
        _volumeSteps = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in ReadLines(volumePath))
        {
            int tab = line.LastIndexOf('\t');
            if (tab <= 0) continue;
            string userId = line.Substring(0, tab);
            if (int.TryParse(line.Substring(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int step) && step > 0)
                _volumeSteps[userId] = step;
        }
    }

    public bool IsMuted(string userId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(userId) && _muted.Contains(userId);
    }

    /// <summary>Index into <see cref="PluginMusicPreferences.VolumeSteps"/>; 0 (full volume) when never chosen.</summary>
    public int VolumeStep(string userId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(userId) && _volumeSteps.TryGetValue(userId, out int step) ? step : 0;
    }

    public void SetMuted(string userId, bool muted)
    {
        ValidateUserId(userId);
        lock (_gate)
        {
            if (_muted.Contains(userId) == muted) return;
            var next = new HashSet<string>(_muted, StringComparer.Ordinal);
            if (muted) next.Add(userId); else next.Remove(userId);
            WriteAtomic(_mutedPath, next.OrderBy(x => x, StringComparer.Ordinal));
            _muted.Clear();
            _muted.UnionWith(next);
        }
    }

    /// <summary>Step 0 is the default and is stored as an absent record.</summary>
    public void SetVolumeStep(string userId, int step)
    {
        ValidateUserId(userId);
        if (step < 0) throw new ArgumentOutOfRangeException(nameof(step));
        lock (_gate)
        {
            _volumeSteps.TryGetValue(userId, out int current);
            if (current == step) return;
            var next = new Dictionary<string, int>(_volumeSteps, StringComparer.Ordinal);
            if (step == 0) next.Remove(userId); else next[userId] = step;
            WriteAtomic(_volumePath, next.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + "\t" + x.Value.ToString(CultureInfo.InvariantCulture)));
            _volumeSteps.Clear();
            foreach (var pair in next) _volumeSteps[pair.Key] = pair.Value;
        }
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
            throw new ArgumentException("An authenticated single-line user ID is required.", nameof(userId));
    }

    private static string[] ReadLines(string path) => File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();

    private static void WriteAtomic(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", lines));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
