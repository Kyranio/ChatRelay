using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatRelay.Host;

namespace ChatRelay.Editor;

/// <summary>Per-process singleton bridging host snapshots to per-view editor MEF components. Subscribers filter by absolute path.</summary>
public sealed class EditorChangesService
{
    public static EditorChangesService Current { get; } = new();

    readonly object _sync = new();
    readonly Dictionary<string, IReadOnlyList<HunkInfo>> _hunksByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Session id from the most recent snapshot. Editor adornments use it when forwarding accept/reject clicks.</summary>
    public string? CurrentSessionId { get; private set; }

    /// <summary>Set by ChatViewModel at host-startup. Args: sessionId, filePath, baselineStart, baselineCount.</summary>
    public Func<string, string, int, int, Task>? AcceptHunkAsync { get; set; }
    public Func<string, string, int, int, Task>? RejectHunkAsync { get; set; }

    /// <summary>Wire-only no-op kept for compatibility — accepts now fold into baseline so there's no marker to invalidate.</summary>
    public Func<string, string, int, int, Task>? InvalidateAcceptedHunkAsync { get; set; }

    /// <summary>Fires when the hunk list for an absolute path changes (added, replaced, or removed).</summary>
    public event Action<string>? HunksChanged;

    public IReadOnlyList<HunkInfo> GetHunks(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return Array.Empty<HunkInfo>();
        lock (_sync)
            return _hunksByPath.TryGetValue(absolutePath, out var hunks) ? hunks : Array.Empty<HunkInfo>();
    }

    /// <summary>Reconciles the cache against a host snapshot, firing HunksChanged for every path whose hunks actually differ.</summary>
    public void Update(SessionChangesSnapshot snapshot)
    {
        if (snapshot is null) return;
        CurrentSessionId = snapshot.SessionId;

        var changedPaths = new List<string>();
        lock (_sync)
        {
            var newSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in snapshot.Proposals)
            {
                newSeen.Add(p.AbsolutePath);
                var newHunks = p.Hunks ?? Array.Empty<HunkInfo>();
                if (!_hunksByPath.TryGetValue(p.AbsolutePath, out var existing) || !HunksEqual(existing, newHunks))
                {
                    _hunksByPath[p.AbsolutePath] = newHunks;
                    changedPaths.Add(p.AbsolutePath);
                }
            }
            foreach (var path in new List<string>(_hunksByPath.Keys))
            {
                if (!newSeen.Contains(path))
                {
                    _hunksByPath.Remove(path);
                    changedPaths.Add(path);
                }
            }
        }

        var handler = HunksChanged;
        if (handler is null) return;
        foreach (var path in changedPaths)
        {
            try { handler(path); }
            catch { }
        }
    }

    /// <summary>Clears cached hunks; called on session change or host disconnect.</summary>
    public void ClearAll()
    {
        List<string> wereTracked;
        lock (_sync)
        {
            wereTracked = new List<string>(_hunksByPath.Keys);
            _hunksByPath.Clear();
        }
        var handler = HunksChanged;
        if (handler is null) return;
        foreach (var path in wereTracked)
        {
            try { handler(path); }
            catch { }
        }
    }

    static bool HunksEqual(IReadOnlyList<HunkInfo> a, IReadOnlyList<HunkInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.BaselineStart != y.BaselineStart || x.BaselineCount != y.BaselineCount) return false;
            if (x.CurrentStart != y.CurrentStart || x.CurrentCount != y.CurrentCount) return false;
            if (x.State != y.State) return false;
            if (!LinesEqual(x.BaselineLines, y.BaselineLines)) return false;
            if (!LinesEqual(x.CurrentLines, y.CurrentLines)) return false;
        }
        return true;
    }

    static bool LinesEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
