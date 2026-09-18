using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerKeybinds;

/// <summary>
/// Port-local durable music preferences: an opt-out set and a per-player volume percent. Reads never
/// touch the disk; every write replaces the whole file atomically and only updates memory on success.
/// </summary>
internal sealed class MusicPreferenceStore
{
    public const int DefaultVolumePercent = 100;

    private readonly string _mutedPath;
    private readonly string _volumePath;
    private readonly object _gate = new();
    private readonly HashSet<string> _muted;
    private readonly Dictionary<string, int> _volumes;

    public MusicPreferenceStore(string mutedPath, string volumePath)
    {
        _mutedPath = mutedPath;
        _volumePath = volumePath;
        _muted = new HashSet<string>(ReadLines(mutedPath).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
        _volumes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in ReadLines(volumePath))
        {
            int tab = line.LastIndexOf('\t');
            if (tab <= 0) continue;
            string userId = line.Substring(0, tab);
            if (int.TryParse(line.Substring(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent)
                && percent >= 0 && percent <= 100 && percent != DefaultVolumePercent)
                _volumes[userId] = percent;
        }
    }

    public bool IsMuted(string userId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(userId) && _muted.Contains(userId);
    }

    /// <summary>0-100; <see cref="DefaultVolumePercent"/> when never chosen.</summary>
    public int VolumePercent(string userId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(userId) && _volumes.TryGetValue(userId, out int percent) ? percent : DefaultVolumePercent;
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

    /// <summary>The default percent is stored as an absent record.</summary>
    public void SetVolumePercent(string userId, int percent)
    {
        ValidateUserId(userId);
        if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        lock (_gate)
        {
            if (VolumePercentUnlocked(userId) == percent) return;
            var next = new Dictionary<string, int>(_volumes, StringComparer.Ordinal);
            if (percent == DefaultVolumePercent) next.Remove(userId); else next[userId] = percent;
            WriteAtomic(_volumePath, next.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + "\t" + x.Value.ToString(CultureInfo.InvariantCulture)));
            _volumes.Clear();
            foreach (var pair in next) _volumes[pair.Key] = pair.Value;
        }
    }

    private int VolumePercentUnlocked(string userId) => _volumes.TryGetValue(userId, out int percent) ? percent : DefaultVolumePercent;

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
