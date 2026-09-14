using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerKeybinds;

/// <summary>Port-local durable opt-outs; reads never touch the disk.</summary>
internal sealed class MusicPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _muted;

    public MusicPreferenceStore(string path)
    {
        _path = path;
        _muted = new HashSet<string>(File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>(), StringComparer.Ordinal);
    }

    public bool IsMuted(string userId)
    {
        lock (_gate) return !string.IsNullOrWhiteSpace(userId) && _muted.Contains(userId);
    }

    public void SetMuted(string userId, bool muted)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("An authenticated single-line user ID is required.", nameof(userId));

        lock (_gate)
        {
            if (_muted.Contains(userId) == muted) return;
            var next = new HashSet<string>(_muted, StringComparer.Ordinal);
            if (muted) next.Add(userId); else next.Remove(userId);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", next.OrderBy(x => x, StringComparer.Ordinal)));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                _muted.Clear();
                _muted.UnionWith(next);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
