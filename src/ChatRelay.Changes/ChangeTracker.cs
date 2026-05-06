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
    ///
    /// <para>
    /// Setting this also (re)creates the file-system watcher so external
    /// edits inside the new workspace can invalidate stale denial entries.
    /// Setting to null disposes the watcher.
    /// </para>
    /// </summary>
    public string? WorkspaceRoot
    {
        get => _workspaceRoot;
        set
        {
            if (string.Equals(_workspaceRoot, value, StringComparison.OrdinalIgnoreCase)) return;
            _workspaceRoot = value;
            RebuildWatcher();
        }
    }
    string? _workspaceRoot;
    WorkspaceWatcher? _watcher;

    void RebuildWatcher()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        if (string.IsNullOrEmpty(_workspaceRoot))
        {
            ExtensionLogger.Info("changes", "Watcher not started: workspace root is empty");
            return;
        }
        if (!Directory.Exists(_workspaceRoot))
        {
            ExtensionLogger.Warn("changes", $"Watcher not started: workspace dir does not exist: {_workspaceRoot}");
            return;
        }
        try
        {
            _watcher = new WorkspaceWatcher(_workspaceRoot!, OnExternalFileChange);
        }
        catch (Exception ex)
        {
            ExtensionLogger.Warn("changes", $"Failed to start workspace watcher: {ex.Message}");
        }
    }

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
            // Phase 1 semantics: per-file deny means "revert this file to
            // its session-baseline content" regardless of whether the user
            // accepted earlier. That makes undo-after-accept work — the
            // previous comparison (Accepted vs LastApplied) treated an
            // already-accepted file as "nothing to deny" because Accept
            // sets them equal, so the user couldn't change their mind.
            //
            // Hunk-level deny (per-hunk decision) is the future inline-
            // editor phase; the FileTracker shape already supports it
            // without touching this method.
            if (string.Equals(f.Baseline, f.LastApplied, StringComparison.Ordinal)) return false;

            // Capture deny payload BEFORE we mutate disk, so a write failure
            // doesn't leave the tracker in a half-state. Counts are vs the
            // baseline so the denied row shows the complete model-edit set
            // the user just rolled back, not a stale 0/0 from an earlier
            // accept.
            var counts = LineDiff.Compute(f.Baseline, f.LastApplied);
            var record = new DeniedChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DeniedAt = DateTime.UtcNow,
                ContentToReapply = f.LastApplied,
                DiskContentAtDeny = f.Baseline,
                LinesAdded = counts.Added,
                LinesRemoved = counts.Removed,
            };

            try
            {
                File.WriteAllText(f.AbsolutePath, f.Baseline);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Deny write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            f.LastApplied = f.Baseline;
            f.Accepted = f.Baseline;     // reset; may previously have equalled the old LastApplied
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
            if (record is null) return false;

            // Refuse if the file's drifted from the post-deny state. The
            // workspace watcher catches most of these proactively, but this
            // belt-and-braces re-read defends against the watcher missing
            // an event (buffer overflow, unsupported file system, …).
            string current;
            try { current = File.ReadAllText(f.AbsolutePath); }
            catch { return false; }
            if (!string.Equals(current, record.DiskContentAtDeny, StringComparison.Ordinal))
            {
                // Drop the now-meaningless entry. Spec: "the removed changes
                // will be cleared from the session file."
                f.Denied.Remove(record);
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
        if (s.Files.TryGetValue(key, out var existing))
        {
            // Already tracking. Two sub-cases:
            //  (a) ExpectingWrite — a previous tool_use this turn already
            //      flagged us; nothing to do beyond keeping the flag set.
            //  (b) Disk has drifted since we last observed it (user edited
            //      between turns and the watcher hasn't caught up yet — or
            //      the file was racy enough that EnsureBaseline arrived
            //      first). Treat that as a fresh-start checkpoint: reset
            //      Baseline to current disk so this turn's diff captures
            //      the user's intermediate work as the starting point,
            //      and drop any leftover denials (they referenced an
            //      obsolete on-disk state).
            if (!existing.ExpectingWrite)
            {
                string? current = TryRead(absolute);
                if (current is not null
                    && !string.Equals(current, existing.LastApplied, StringComparison.Ordinal)
                    && !string.Equals(current, existing.Baseline, StringComparison.Ordinal))
                {
                    ExtensionLogger.Info("changes",
                        $"Refreshing Baseline for {absolute} — disk drifted before tool_use");
                    existing.Baseline = current;
                    existing.Accepted = current;
                    existing.LastApplied = current;
                    existing.IsAccepted = false;
                    existing.Denied.Clear();
                }
            }
            existing.ExpectingWrite = true;
            return;
        }

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
            ExpectingWrite = true,       // tool will write between now and tool_result
        };
    }

    static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return null; }
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
            if (s.Files.TryGetValue(key, out f))
                f.ExpectingWrite = false;
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
        // Tool finished — disk is post-write, the watcher's content-match
        // check (against LastApplied) will correctly identify subsequent
        // events for this file as our own writes.
        f.ExpectingWrite = false;
        // Subsequent model edit invalidates any prior denial whose post-deny
        // disk state no longer matches reality. Per spec: "Once the file is
        // modified ... the removed changes will be cleared from the session
        // file." The same rule applies whether the modification came from
        // the model (this path) or the user (OnExternalFileChange below).
        f.Denied.RemoveAll(d => !string.Equals(d.DiskContentAtDeny, f.LastApplied, StringComparison.Ordinal));
    }

    /// <summary>
    /// Workspace watcher entry point. Runs whenever <see cref="WorkspaceWatcher"/>
    /// reports a file change inside the watched root, on a background thread.
    /// Drops denial entries whose post-deny disk state has drifted.
    ///
    /// <para>
    /// Skips if disk content matches a known clean state (<c>LastApplied</c>
    /// or <c>Baseline</c>) — that's almost certainly our own write echoing
    /// back through the watcher and shouldn't invalidate anything.
    /// </para>
    /// </summary>
    void OnExternalFileChange(string absolutePath)
    {
        // File-system events fire on a thread-pool thread before the writer
        // has necessarily released its lock. Retry the read a few times
        // with a tiny backoff so we don't silently drop a real edit just
        // because we raced VS's save handle.
        string? content = TryReadWithRetry(absolutePath);
        if (content is null) return;

        var key = NormalisePath(absolutePath);
        var changedSessions = new List<string>();
        bool anyTracked = false;
        foreach (var kv in _sessions)
        {
            var session = kv.Value;
            lock (session.Sync)
            {
                if (!session.Files.TryGetValue(key, out var f)) continue;
                anyTracked = true;

                // Tool just emitted a use-event but tool_result hasn't
                // arrived to update LastApplied — the on-disk write is the
                // model's, not the user's. Skip so we don't mis-classify it
                // as external and clobber state.
                if (f.ExpectingWrite)
                {
                    ExtensionLogger.Debug("changes", $"Watcher echo (ExpectingWrite): {absolutePath}");
                    continue;
                }

                // Disk matches a clean state we know about → echo of our own
                // write or a no-op (e.g. tool that touched but didn't modify).
                // Don't disturb any denial entries.
                if (string.Equals(content, f.LastApplied, StringComparison.Ordinal))
                {
                    ExtensionLogger.Debug("changes", $"Watcher echo (LastApplied match): {absolutePath}");
                    continue;
                }
                if (string.Equals(content, f.Baseline, StringComparison.Ordinal))
                {
                    ExtensionLogger.Debug("changes", $"Watcher echo (Baseline match): {absolutePath}");
                    continue;
                }

                // External modification. Drop denial records whose post-deny
                // expected state doesn't match the new disk content.
                var before = f.Denied.Count;
                var removed = f.Denied.RemoveAll(
                    d => !string.Equals(d.DiskContentAtDeny, content, StringComparison.Ordinal));
                ExtensionLogger.Info("changes",
                    $"External edit on {absolutePath}: dropped {removed}/{before} denial(s)");

                // If the file no longer has a meaningful tracker state
                // (no open/accepted proposal AND no surviving denials),
                // drop the entry entirely. Without this, a later Claude
                // edit would diff against the obsolete pre-user-edit
                // Baseline — making the user's removed lines invisible
                // in the +N/−M counts.
                bool entryDropped = false;
                if (!f.HasProposal && f.Denied.Count == 0)
                {
                    session.Files.Remove(key);
                    entryDropped = true;
                    ExtensionLogger.Info("changes",
                        $"Dropped tracker entry for {absolutePath} (clean state after external edit)");
                }

                if (removed > 0 || entryDropped) changedSessions.Add(kv.Key);
            }
        }
        if (!anyTracked)
            ExtensionLogger.Debug("changes", $"Watcher fired for untracked path: {absolutePath}");
        foreach (var sid in changedSessions) FireNotify(sid);
    }

    // Up to 5 attempts × 50ms gap = ~250ms of grace for the writer to
    // release its handle. VS's save sequence (write temp → rename) typically
    // completes in low single-digit ms, so this is generous. Returns null
    // if every attempt failed — caller treats that as "ignore this event."
    static string? TryReadWithRetry(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Watcher read failed for {path}: {ex.Message}");
                return null;
            }
        }
        ExtensionLogger.Warn("changes", $"Watcher read gave up (locked) for {path}");
        return null;
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
                            // Stale denials are dropped from f.Denied
                            // outright (in OnExternalFileChange / Update-
                            // LastApplied / RedoDenial) — by the time we
                            // hit this projection, anything still here is
                            // redoable. Wire field kept for forward-compat.
                            CanRedo: true))
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
