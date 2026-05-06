using System;
using System.Collections.Generic;
using ChatRelay.Host;

namespace ChatRelay.Editor;

/// <summary>
/// Per-process singleton bridging host-side <see cref="SessionChangesSnapshot"/>
/// updates to the editor MEF components that need per-file hunk data
/// (Phase 4.2+ adornment work).
///
/// <para>
/// The chat <see cref="ChatRelay.Chat.ViewModels.ChatViewModel"/> already
/// subscribes to <see cref="HostClient.ChangesUpdated"/> for its own
/// file-level proposal list — it pushes the same snapshots into this
/// service so editor-side consumers see hunks without each having to
/// own its own RPC subscription. Subscribers fan out by absolute path:
/// each editor-view manager listens for its own file's changes.
/// </para>
///
/// <para>
/// Lazy-static so MEF components instantiated at editor-view creation
/// (potentially before the chat tool window opens) always get a real
/// service object — they just see empty hunk lists until the host
/// starts producing snapshots.
/// </para>
/// </summary>
public sealed class EditorChangesService
{
    public static EditorChangesService Current { get; } = new();

    readonly object _sync = new();
    readonly Dictionary<string, IReadOnlyList<HunkInfo>> _hunksByPath
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fires when the hunk list for the given absolute path changes —
    /// either updated, replaced, or cleared (file no longer in any
    /// proposal). Subscribers filter on the path argument; we don't
    /// per-path-key the events to keep the service simple.
    /// </summary>
    public event Action<string>? HunksChanged;

    /// <summary>
    /// Returns the latest hunks for <paramref name="absolutePath"/>, or an
    /// empty list if the file isn't currently in any proposal. Safe to
    /// call from any thread.
    /// </summary>
    public IReadOnlyList<HunkInfo> GetHunks(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return Array.Empty<HunkInfo>();
        lock (_sync)
        {
            return _hunksByPath.TryGetValue(absolutePath, out var hunks)
                ? hunks
                : Array.Empty<HunkInfo>();
        }
    }

    /// <summary>
    /// Reconciles the cache against the latest host snapshot. Diffs the
    /// new state against the prior, fires <see cref="HunksChanged"/> for
    /// every path whose hunk set changed (added, replaced, or removed).
    /// Called from <see cref="ChatRelay.Chat.ViewModels.ChatViewModel"/>'s
    /// snapshot-ingest path so chat and editor views observe the same
    /// data at the same time.
    /// </summary>
    public void Update(SessionChangesSnapshot snapshot)
    {
        if (snapshot is null) return;

        var changedPaths = new List<string>();
        lock (_sync)
        {
            // Build the new map keyed by path; track which paths have
            // genuinely changed so we don't fire spurious events on
            // re-renders of the same hunk list.
            var newSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in snapshot.Proposals)
            {
                newSeen.Add(p.AbsolutePath);
                var newHunks = p.Hunks ?? Array.Empty<HunkInfo>();
                if (!_hunksByPath.TryGetValue(p.AbsolutePath, out var existing)
                    || !HunksEqual(existing, newHunks))
                {
                    _hunksByPath[p.AbsolutePath] = newHunks;
                    changedPaths.Add(p.AbsolutePath);
                }
            }
            // Drop entries whose file is no longer in any proposal.
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
            catch { /* subscriber threw — don't poison the rest */ }
        }
    }

    /// <summary>
    /// Clears all cached hunks. Called when the session changes or the
    /// host disconnects so editor views don't keep stale state.
    /// </summary>
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
            if (x.BaselineStart != y.BaselineStart) return false;
            if (x.BaselineCount != y.BaselineCount) return false;
            if (x.CurrentStart != y.CurrentStart) return false;
            if (x.CurrentCount != y.CurrentCount) return false;
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
