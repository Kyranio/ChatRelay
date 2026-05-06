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
    /// Tells the tracker which model is currently driving the session, so
    /// future tool-use observations can stamp the model onto the file's
    /// <see cref="FileTracker.LastModel"/>. Wired from <c>HostService</c>'s
    /// <c>ModelInfo</c> event handler. Calling with a null/empty value
    /// clears the stored model.
    /// </summary>
    public void SetCurrentModel(string sessionId, string? modelDisplayName)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync)
        {
            s.CurrentModel = string.IsNullOrEmpty(modelDisplayName) ? null : modelDisplayName;
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
            // Whole-file accept: lock in the current LastApplied as Accepted,
            // AND populate AcceptedHunks with every current hunk so per-hunk
            // reject after a whole-file accept can correctly identify which
            // hunks need to be reverted in Accepted vs. just LastApplied.
            f.Accepted = f.LastApplied;
            f.IsAccepted = true;
            f.AcceptedHunks.Clear();
            foreach (var h in LineDiff.ComputeHunks(f.Baseline, f.LastApplied))
                f.AcceptedHunks.Add(MakeKey(h));
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    /// <summary>
    /// Accepts a single hunk, identified by its position in the baseline
    /// (<paramref name="baselineStart"/> + <paramref name="baselineCount"/>).
    /// Splices the hunk's model-side lines into <c>Accepted</c> at the
    /// position adjusted for already-accepted hunks; no disk write
    /// (<c>LastApplied</c> already has the model's content). No-op if the
    /// hunk doesn't exist in the current diff or is already accepted.
    /// </summary>
    public bool AcceptHunk(string sessionId, string filePath, int baselineStart, int baselineCount)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;

            var hunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
            var hunk = FindHunkByCoords(hunks, baselineStart, baselineCount);
            if (hunk is null) return false;
            var key = MakeKey(hunk.Value);
            if (f.AcceptedHunks.Contains(key)) return false;   // already accepted

            // Project the hunk's baseline position into Accepted-coordinates
            // by shifting for previously-accepted hunks that come before it.
            int projected = ProjectBaselineToAccepted(f, hunks, baselineStart);
            f.Accepted = LineDiff.SpliceLines(f.Accepted, projected, hunk.Value.OldCount, hunk.Value.NewLines);
            f.AcceptedHunks.Add(key);

            // If every hunk is now accepted, the file matches LastApplied —
            // mirror the whole-file-accepted IsAccepted flag.
            if (string.Equals(f.Accepted, f.LastApplied, StringComparison.Ordinal))
                f.IsAccepted = true;

            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    /// <summary>
    /// Rejects a single hunk, identified by baseline coordinates. Splices
    /// the baseline content back into LastApplied (and into Accepted if the
    /// hunk had been accepted), writes the file to disk, and stashes a
    /// denial record so the user can redo. Works on both open and accepted
    /// hunks; the file's region returns to baseline either way.
    /// </summary>
    public bool RejectHunk(string sessionId, string filePath, int baselineStart, int baselineCount)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;

            var hunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
            var hunkOpt = FindHunkByCoords(hunks, baselineStart, baselineCount);
            if (hunkOpt is null) return false;
            var hunk = hunkOpt.Value;
            var key = MakeKey(hunk);

            bool wasAccepted = f.AcceptedHunks.Contains(key);

            // Capture pre-reject snapshot for the redo path.
            var preRejectLastApplied = f.LastApplied;

            // Splice baseline content into LastApplied at the hunk's
            // current position. Effect: that region now matches Baseline.
            var newLastApplied = LineDiff.SpliceLines(
                f.LastApplied, hunk.NewStart, hunk.NewCount, hunk.OldLines);

            // If the hunk had been accepted, also revert it in Accepted —
            // otherwise Accepted would still contain NewLines at that
            // position and snapshot diffs would mis-classify the file.
            string newAccepted = f.Accepted;
            if (wasAccepted)
            {
                int projected = ProjectBaselineToAccepted(f, hunks, baselineStart);
                newAccepted = LineDiff.SpliceLines(newAccepted, projected, hunk.NewCount, hunk.OldLines);
            }

            try
            {
                File.WriteAllText(f.AbsolutePath, newLastApplied);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Hunk reject write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            f.LastApplied = newLastApplied;
            f.Accepted = newAccepted;
            f.AcceptedHunks.Remove(key);
            f.IsAccepted = false;

            // The denial is whole-file: ContentToReapply restores the file
            // to its pre-reject state in one shot. Simpler than per-hunk
            // bookkeeping and works regardless of subsequent operations.
            int linesAdded = hunk.NewCount;
            int linesRemoved = hunk.OldCount;
            f.Denied.Add(new DeniedChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DeniedAt = DateTime.UtcNow,
                ContentToReapply = preRejectLastApplied,
                DiskContentAtDeny = newLastApplied,
                LinesAdded = linesAdded,
                LinesRemoved = linesRemoved,
            });
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    // Coordinate-only lookup for the wire-dispatched accept/reject RPCs:
    // the shell only sends (BaselineStart, BaselineCount), so we locate
    // the hunk by those alone and build the full HunkKey (including
    // content) from the resolved hunk.
    static LineDiff.Hunk? FindHunkByCoords(IReadOnlyList<LineDiff.Hunk> hunks, int baselineStart, int baselineCount)
    {
        for (int i = 0; i < hunks.Count; i++)
            if (hunks[i].OldStart == baselineStart && hunks[i].OldCount == baselineCount)
                return hunks[i];
        return null;
    }

    /// <summary>
    /// Builds a content-aware <see cref="HunkKey"/> for the given hunk.
    /// Same coordinates + same new-side content → same key. The content
    /// term is what makes the prior accept marker NOT carry forward when
    /// the model produces a different hunk at the same baseline coords
    /// on a follow-up turn.
    /// </summary>
    static HunkKey MakeKey(LineDiff.Hunk h) =>
        new(h.OldStart, h.OldCount, JoinLines(h.NewLines));

    static string JoinLines(IReadOnlyList<string> lines) => string.Join("\n", lines);

    // Maps a Baseline-line-number to its corresponding line in Accepted, by
    // walking ALL hunks the model produced (in baseline order) and shifting
    // for those the user has accepted that lie strictly before the target.
    static int ProjectBaselineToAccepted(FileTracker f, IReadOnlyList<LineDiff.Hunk> hunks, int baselineStart)
    {
        int projected = baselineStart;
        foreach (var h in hunks.OrderBy(x => x.OldStart))
        {
            if (h.OldStart >= baselineStart) break;
            if (f.AcceptedHunks.Contains(MakeKey(h)))
                projected += h.NewCount - h.OldCount;
        }
        return projected;
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
            f.AcceptedHunks.Clear();     // whole-file revert wipes any per-hunk accepts too
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
                f.AcceptedHunks.Clear();
                foreach (var h in LineDiff.ComputeHunks(f.Baseline, f.LastApplied))
                    f.AcceptedHunks.Add(MakeKey(h));
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
                // Per-hunk accepts that survived bulk-deny stay valid against
                // the new (smaller) hunk set; prune any stale keys.
                PruneStaleAcceptedHunksLocked(f);
                f.Denied.Add(record);
                any = true;
            }
        }
        if (any) FireNotify(sessionId);
        return any;
    }

    // Drops AcceptedHunks entries that no longer correspond to a hunk in
    // the current diff(Baseline, LastApplied). Called whenever LastApplied
    // changes (model edits, redo, bulk deny) so per-hunk accept state can't
    // outlive the hunks themselves.
    static void PruneStaleAcceptedHunksLocked(FileTracker f)
    {
        if (f.AcceptedHunks.Count == 0) return;
        var hunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
        var valid = new HashSet<HunkKey>();
        foreach (var h in hunks) valid.Add(MakeKey(h));
        f.AcceptedHunks.RemoveWhere(k => !valid.Contains(k));
    }

    // Recomputes the Accepted blob from Baseline + the AcceptedHunks set.
    // Splices the model's new lines in for each accepted hunk, applied in
    // reverse baseline-order so earlier coordinates stay valid as we go.
    static string ApplyAcceptedHunks(FileTracker f)
    {
        if (f.AcceptedHunks.Count == 0) return f.Baseline;
        var hunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
        var byKey = new Dictionary<HunkKey, LineDiff.Hunk>();
        foreach (var h in hunks) byKey[MakeKey(h)] = h;

        var content = f.Baseline;
        // Reverse baseline-order so each splice's coordinates still address
        // unmodified content (earlier hunks haven't been applied yet).
        var ordered = f.AcceptedHunks
            .Where(byKey.ContainsKey)
            .OrderByDescending(k => k.BaselineStart)
            .Select(k => byKey[k]);
        foreach (var h in ordered)
            content = LineDiff.SpliceLines(content, h.OldStart, h.OldCount, h.NewLines);
        return content;
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
                    existing.AcceptedHunks.Clear();
                    existing.Denied.Clear();
                }
            }
            existing.ExpectingWrite = true;
            // Stamp the session's current model onto the file. If a turn
            // starts before any ModelInfo has arrived, LastModel stays at
            // its prior value (or null on the very first tool_use), and
            // gets overwritten the next time around.
            if (s.CurrentModel is not null) existing.LastModel = s.CurrentModel;
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
            LastModel = s.CurrentModel,  // null until ModelInfo lands; surfaced in snapshot
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

        // Per-hunk accepts we held may now reference hunks that don't exist
        // in the new diff — drop stale keys, then recompute the Accepted
        // blob from Baseline + the surviving accepted hunks so it stays
        // internally consistent with AcceptedHunks.
        PruneStaleAcceptedHunksLocked(f);
        f.Accepted = ApplyAcceptedHunks(f);
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
    internal void OnExternalFileChange(string absolutePath)
    {
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

                // Read disk INSIDE the lock. Reading before lock-acquire
                // would let a concurrent Accept/Deny/Redo settle disk +
                // LastApplied between our read and our state check —
                // we'd then compare a stale content snapshot against the
                // freshly-updated LastApplied, mistakenly classifying the
                // operation's own write as an external edit and dropping
                // denials that were correctly applied. File-system events
                // can also fire before the writer has released its lock,
                // hence the retry-on-IOException loop in TryReadWithRetry.
                string? content = TryReadWithRetry(absolutePath);
                if (content is null) continue;

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
                // FileShare.ReadWrite so a concurrent writer (the user
                // saving in VS, or even the model's tool finishing its
                // write) doesn't fail us with a sharing violation.
                // Worst case: we read a partially-written buffer; the
                // content-comparison logic upstream falls through as
                // "external edit" and the next event resolves it.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
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
                var hunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
                var hunkInfos = new List<HunkInfo>(hunks.Count);
                foreach (var h in hunks)
                {
                    var key = MakeKey(h);
                    hunkInfos.Add(new HunkInfo(
                        BaselineStart: h.OldStart,
                        BaselineCount: h.OldCount,
                        CurrentStart: h.NewStart,
                        CurrentCount: h.NewCount,
                        BaselineLines: h.OldLines,
                        CurrentLines: h.NewLines,
                        State: f.AcceptedHunks.Contains(key) ? "accepted" : "open",
                        Model: f.LastModel));
                }
                proposals.Add(new ChangeProposal(
                    FilePath: f.DisplayPath,
                    AbsolutePath: f.AbsolutePath,
                    LinesAdded: counts.Added,
                    LinesRemoved: counts.Removed,
                    State: f.IsAccepted && !f.HasOpenChanges ? "accepted" : "open",
                    Hunks: hunkInfos));
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

        /// <summary>
        /// Display name of the model the host most recently announced for
        /// this session via <c>onModelInfo</c>. Stamped onto each
        /// <see cref="FileTracker"/> when it sees a tool_use, then surfaced
        /// in the snapshot so the editor adornment can show "Edited by X".
        /// Null until the first <c>ModelInfo</c> event lands.
        /// </summary>
        public string? CurrentModel;
    }
}
