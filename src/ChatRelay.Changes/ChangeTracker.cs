using System.Collections.Concurrent;
using ChatRelay.Host;
using ChatRelay.Logging;

namespace ChatRelay.Changes;

/// <summary>
/// In-memory, per-session change tracker. The host process owns one
/// instance for its entire lifetime; closing VS terminates the host and
/// wipes everything — that's the volatility guarantee from the spec.
///
/// <para>
/// Thread-safety: tool-use observations arrive on adapter background
/// threads, RPC calls from the shell arrive on JsonRpc threads.
/// All mutating operations take the per-session lock; reads on the
/// snapshot path are protected by the same lock to give the wire DTO a
/// consistent view.
/// </para>
/// </summary>
public sealed class ChangeTracker
{
    readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    /// <summary>
    /// Optional callback fired after any state mutation so the host can
    /// emit <c>onChangesUpdated</c> with the fresh snapshot. Set by
    /// <see cref="HostService"/> at startup.
    /// </summary>
    public Action<string, SessionChangesSnapshot>? Notify { get; set; }

    /// <summary>
    /// Workspace root used to scope path filtering and produce display
    /// paths. Updated by <c>setWorkspace</c> on the host. Null = no
    /// workspace, in which case nothing is tracked (we don't want to
    /// follow writes outside any project).
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    public SessionChangesSnapshot Snapshot(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync)
        {
            return BuildSnapshotLocked(sessionId, s);
        }
    }

    /// <summary>
    /// Acts on a tool-use observation from any adapter. Internally filters
    /// to file-mutating tools and to paths inside the current workspace —
    /// out-of-tree writes are silently ignored per the spec.
    /// </summary>
    public void Observe(string sessionId, ToolCallObservation obs)
    {
        if (!FileMutatingTools.IsKnown(obs.ToolName)) return;
        var path = FileMutatingTools.TryExtractPath(obs.ToolName, obs.InputJson);
        if (string.IsNullOrEmpty(path)) return;

        string absolute;
        try { absolute = Path.GetFullPath(path!); }
        catch { return; }
        if (!IsInsideWorkspace(absolute)) return;

        var s = GetOrCreate(sessionId);
        lock (s.Sync)
        {
            switch (obs.Phase)
            {
                case ToolCallPhase.Requested:
                    EnsureBaselineLocked(s, absolute);
                    break;
                case ToolCallPhase.Completed:
                    UpdateLastAppliedLocked(s, absolute);
                    break;
            }
        }
        FireNotify(sessionId);
    }

    public bool Accept(string sessionId, string filePath)
    {
        var s = GetOrCreate(sessionId);
        bool changed;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            if (!f.HasProposal) return false;
            // Whole-file accept: lock in the current LastApplied as Accepted.
            f.Accepted = f.LastApplied;
            f.IsAccepted = true;
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    public bool Deny(string sessionId, string filePath)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            // Nothing to deny if Accepted already matches LastApplied.
            if (string.Equals(f.Accepted, f.LastApplied, StringComparison.Ordinal)) return false;

            // Capture deny payload BEFORE we mutate disk, so a write failure
            // doesn't leave the tracker in a half-state.
            var counts = LineDiff.Compute(f.Accepted, f.LastApplied);
            var record = new DeniedChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DeniedAt = DateTime.UtcNow,
                ContentToReapply = f.LastApplied,
                DiskContentAtDeny = f.Accepted,
                LinesAdded = counts.Added,
                LinesRemoved = counts.Removed,
            };

            try
            {
                File.WriteAllText(f.AbsolutePath, f.Accepted);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Deny write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            f.LastApplied = f.Accepted;
            f.IsAccepted = false;
            f.Denied.Add(record);
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    public bool RedoDenial(string sessionId, string filePath, string denialId)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            var record = f.Denied.FirstOrDefault(d => d.Id == denialId);
            if (record is null || record.IsStale) return false;

            // Refuse if the file's drifted from the post-deny state. The
            // FileSystemWatcher path also marks records stale on external
            // edits; this is a belt-and-braces check.
            string current;
            try { current = File.ReadAllText(f.AbsolutePath); }
            catch { return false; }
            if (!string.Equals(current, record.DiskContentAtDeny, StringComparison.Ordinal))
            {
                record.IsStale = true;
                changed = true;
                goto done;
            }

            try { File.WriteAllText(f.AbsolutePath, record.ContentToReapply); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Redo write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            // Re-applying lifts the file back to a proposal state. We do
            // NOT re-mark it accepted — the user has to decide again.
            f.LastApplied = record.ContentToReapply;
            f.IsAccepted = false;
            f.Denied.Remove(record);
            changed = true;
        }
        done:
        if (changed) FireNotify(sessionId);
        return changed;
    }

    public bool AcceptAllOpen(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        bool any = false;
        lock (s.Sync)
        {
            foreach (var f in s.Files.Values)
            {
                if (!f.HasOpenChanges) continue;
                f.Accepted = f.LastApplied;
                f.IsAccepted = true;
                any = true;
            }
        }
        if (any) FireNotify(sessionId);
        return any;
    }

    public bool DenyAllOpen(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        bool any = false;
        lock (s.Sync)
        {
            foreach (var f in s.Files.Values)
            {
                if (!f.HasOpenChanges) continue;
                var counts = LineDiff.Compute(f.Accepted, f.LastApplied);
                var record = new DeniedChangeRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DeniedAt = DateTime.UtcNow,
                    ContentToReapply = f.LastApplied,
                    DiskContentAtDeny = f.Accepted,
                    LinesAdded = counts.Added,
                    LinesRemoved = counts.Removed,
                };
                try { File.WriteAllText(f.AbsolutePath, f.Accepted); }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("changes", $"Bulk deny write failed for {f.AbsolutePath}: {ex.Message}");
                    continue;
                }
                f.LastApplied = f.Accepted;
                f.IsAccepted = false;
                f.Denied.Add(record);
                any = true;
            }
        }
        if (any) FireNotify(sessionId);
        return any;
    }

    public int CountOpen(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync) return s.Files.Values.Count(f => f.HasOpenChanges);
    }

    public void ClearSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _)) FireNotify(sessionId);
    }

    // -- Internals -------------------------------------------------------

    SessionState GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new SessionState());

    bool IsInsideWorkspace(string absolute)
    {
        if (string.IsNullOrEmpty(WorkspaceRoot)) return false;
        var root = Path.GetFullPath(WorkspaceRoot!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rooted = absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rooted.Length >= root.Length
            && rooted.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (rooted.Length == root.Length
                || rooted[root.Length] == Path.DirectorySeparatorChar
                || rooted[root.Length] == Path.AltDirectorySeparatorChar);
    }

    static string NormalisePath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }

    void EnsureBaselineLocked(SessionState s, string absolute)
    {
        var key = NormalisePath(absolute);
        if (s.Files.ContainsKey(key)) return;   // already tracking — preserve original baseline
        string baseline = string.Empty;
        if (File.Exists(absolute))
        {
            try { baseline = File.ReadAllText(absolute); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Baseline read failed for {absolute}: {ex.Message}");
                return;
            }
        }
        s.Files[key] = new FileTracker
        {
            AbsolutePath = absolute,
            DisplayPath = MakeDisplay(absolute),
            Baseline = baseline,
            Accepted = baseline,         // nothing accepted yet
            LastApplied = baseline,      // pre-write — model hasn't run yet
        };
    }

    void UpdateLastAppliedLocked(SessionState s, string absolute)
    {
        var key = NormalisePath(absolute);
        if (!s.Files.TryGetValue(key, out var f))
        {
            // Tool_result with no prior tool_use — race or unknown tool.
            // Treat current disk as both baseline and applied so we at
            // least don't crash; user-facing diff will be 0/0 until the
            // next observed write.
            EnsureBaselineLocked(s, absolute);
            return;
        }
        try
        {
            f.LastApplied = File.Exists(absolute) ? File.ReadAllText(absolute) : string.Empty;
        }
        catch (Exception ex)
        {
            ExtensionLogger.Warn("changes", $"Post-write read failed for {absolute}: {ex.Message}");
        }
    }

    string MakeDisplay(string absolute)
    {
        if (string.IsNullOrEmpty(WorkspaceRoot)) return absolute;
        var root = Path.GetFullPath(WorkspaceRoot!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) && absolute.Length > root.Length + 1)
            return absolute[(root.Length + 1)..];
        return absolute;
    }

    SessionChangesSnapshot BuildSnapshotLocked(string sessionId, SessionState s)
    {
        var proposals = new List<ChangeProposal>();
        var denials = new List<DenialGroup>();

        foreach (var f in s.Files.Values.OrderBy(x => x.DisplayPath, StringComparer.OrdinalIgnoreCase))
        {
            if (f.HasProposal)
            {
                var counts = LineDiff.Compute(f.Baseline, f.LastApplied);
                proposals.Add(new ChangeProposal(
                    FilePath: f.DisplayPath,
                    AbsolutePath: f.AbsolutePath,
                    LinesAdded: counts.Added,
                    LinesRemoved: counts.Removed,
                    State: f.IsAccepted && !f.HasOpenChanges ? "accepted" : "open"));
            }

            if (f.Denied.Count > 0)
            {
                denials.Add(new DenialGroup(
                    FilePath: f.DisplayPath,
                    AbsolutePath: f.AbsolutePath,
                    Entries: f.Denied
                        .Select(d => new DeniedChangeSummary(
                            Id: d.Id,
                            LinesAdded: d.LinesAdded,
                            LinesRemoved: d.LinesRemoved,
                            DeniedAt: d.DeniedAt,
                            CanRedo: !d.IsStale))
                        .ToList()));
            }
        }

        return new SessionChangesSnapshot(sessionId, proposals, denials);
    }

    void FireNotify(string sessionId)
    {
        var notify = Notify;
        if (notify is null) return;
        SessionChangesSnapshot snap;
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            lock (s.Sync) snap = BuildSnapshotLocked(sessionId, s);
        }
        else
        {
            snap = new SessionChangesSnapshot(sessionId, [], []);
        }
        try { notify(sessionId, snap); }
        catch (Exception ex) { ExtensionLogger.Warn("changes", "Notify threw: " + ex.Message); }
    }

    sealed class SessionState
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, FileTracker> Files = new(StringComparer.OrdinalIgnoreCase);
    }
}
