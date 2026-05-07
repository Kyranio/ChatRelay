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
    bool _enableFileSystemWatcher = true;

    /// <summary>
    /// Controls whether <see cref="WorkspaceRoot"/> spins up the
    /// background <see cref="WorkspaceWatcher"/>. Default true. Tests
    /// set this to false before assigning <see cref="WorkspaceRoot"/>
    /// so they don't race against the watcher firing partial-read
    /// events during synchronous file writes — the test path calls
    /// <see cref="OnExternalFileChange"/> directly anyway, so the
    /// watcher would only add noise.
    /// </summary>
    public bool EnableFileSystemWatcher
    {
        get => _enableFileSystemWatcher;
        set
        {
            if (_enableFileSystemWatcher == value) return;
            _enableFileSystemWatcher = value;
            RebuildWatcher();
        }
    }

    void RebuildWatcher()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        if (!_enableFileSystemWatcher) return;
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
    /// Rejects a single OPEN hunk, identified by baseline coordinates.
    /// Splices the baseline content back into LastApplied, writes the
    /// file to disk, and stashes a denial record so the user can redo.
    ///
    /// <para>
    /// Refuses (returns false) on accepted hunks. Per the simplified
    /// model, accepted hunks are set in stone — the user reverts via the
    /// editor's own undo stack rather than through this extension. This
    /// guard prevents both the inline editor and chat-side bulk paths
    /// from clobbering an accepted change.
    /// </para>
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

            // Accepted hunks are set in stone — refuse rather than revert.
            if (f.AcceptedHunks.Contains(key)) return false;

            // Capture pre-reject snapshot for the redo path.
            var preRejectLastApplied = f.LastApplied;

            // Splice baseline content into LastApplied at the hunk's
            // current position. Effect: that region now matches Baseline.
            var newLastApplied = LineDiff.SpliceLines(
                f.LastApplied, hunk.NewStart, hunk.NewCount, hunk.OldLines);

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
            // Accepted blob is unaffected — we refused on accepted hunks
            // earlier, so this hunk wasn't in AcceptedHunks. Other accepted
            // hunks' contributions stay intact.
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

    /// <summary>
    /// Drops the accept-marker for one hunk and folds the model's accepted
    /// content into <see cref="FileTracker.Baseline"/> so the hunk
    /// disappears from the diff entirely — not re-classified as "open".
    /// Sent by the editor when the user types inside an accepted hunk's
    /// current line range.
    ///
    /// <para>
    /// Why fold into Baseline rather than just drop the key: the user's
    /// expectation when modifying an accepted region is "this is mine
    /// now, stop tracking it." Re-opening would put accept/reject UI back
    /// over the user's own typing — wrong. Splicing the hunk's
    /// <c>NewLines</c> into Baseline at <c>(BaselineStart, BaselineCount)</c>
    /// makes Baseline reflect what disk actually has (the model's
    /// accepted content), so <c>diff(Baseline, LastApplied) = ∅</c> for
    /// that region and nothing surfaces in the next snapshot.
    /// </para>
    ///
    /// <para>
    /// Other accepted hunks AFTER this one in baseline coords need their
    /// <see cref="HunkKey.BaselineStart"/> shifted by
    /// <c>(NewLines.Count - OldCount)</c> — Baseline just grew or shrank
    /// by that much. Keys before the splice point are unaffected.
    /// </para>
    ///
    /// <para>
    /// No-op if the file isn't tracked, the hunk doesn't exist, or it
    /// wasn't accepted.
    /// </para>
    /// </summary>
    public bool InvalidateAcceptedHunk(string sessionId, string filePath, int baselineStart, int baselineCount)
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
            if (!f.AcceptedHunks.Remove(key)) return false;

            // Splice the hunk's new-side content into Baseline at the
            // hunk's baseline position. Baseline now matches what's on
            // disk for this region.
            f.Baseline = LineDiff.SpliceLines(f.Baseline, baselineStart, baselineCount, hunk.NewLines);
            ShiftAcceptedHunksAfterLocked(f, baselineStart + baselineCount, hunk.NewLines.Count - hunk.OldCount);

            // Re-derive Accepted from the updated Baseline + remaining
            // accepted hunks. Note: this hunk's contribution is already
            // baked into Baseline, so it's not double-applied.
            f.Accepted = ApplyAcceptedHunks(f);
            // If Baseline now equals LastApplied, no proposal remains —
            // IsAccepted stays false (we don't auto-flag a vanished file
            // as "accepted").
            f.IsAccepted = false;
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

    // Maps a Baseline-line-number to its corresponding line in Accepted by
    // walking ALL hunks the model produced and shifting for those the user
    // has accepted that lie strictly before the target. ComputeHunks
    // already returns hunks in OldStart order so no sort is needed.
    static int ProjectBaselineToAccepted(FileTracker f, IReadOnlyList<LineDiff.Hunk> hunks, int baselineStart)
    {
        int projected = baselineStart;
        foreach (var h in hunks)
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
            // Per-file deny under the simplified model: revert OPEN hunks
            // only, leave accepted hunks intact (they're set in stone).
            // Effectively a per-file "deny all open" scoped to this file.
            // No-op if there are no open changes (Accepted already covers
            // everything that's on disk).
            if (!f.HasOpenChanges) return false;

            // Capture deny payload BEFORE we mutate disk. Counts reflect
            // ONLY the open delta (Accepted → LastApplied), so the denied
            // row shows what got rolled back rather than the full
            // accepted-plus-open history.
            var counts = LineDiff.Compute(f.Accepted, f.LastApplied);
            var record = new DeniedChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DeniedAt = DateTime.UtcNow,
                ContentToReapply = f.LastApplied,    // pre-deny snapshot for redo
                DiskContentAtDeny = f.Accepted,      // post-deny disk state (accepted preserved)
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
            // f.Accepted unchanged — accepted hunks remain spliced in.
            // f.AcceptedHunks unchanged — the keys still match against
            // diff(Baseline, new LastApplied=Accepted).
            // f.IsAccepted reflects "everything on disk is accepted" now
            // (since LastApplied == Accepted) — true iff there are any
            // accepted hunks in this file.
            f.IsAccepted = f.AcceptedHunks.Count > 0;
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
            // workspace watcher catches most drift proactively, but this
            // belt-and-braces re-read defends against missed events.
            string current;
            try { current = File.ReadAllText(f.AbsolutePath); }
            catch { return false; }

            if (!string.Equals(current, record.DiskContentAtDeny, StringComparison.Ordinal))
            {
                // Drop the now-meaningless entry. Spec: "the removed changes
                // will be cleared from the session file."
                f.Denied.Remove(record);
                changed = true;
            }
            else
            {
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
        }
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

    /// <summary>
    /// Folds a saved external edit into the file's tracker state under
    /// the simplified "users only remove" model:
    /// <list type="bullet">
    ///   <item>Open hunks are allowed to grow / shrink to absorb user
    ///   typing inside them.</item>
    ///   <item>Accepted hunks the user hasn't touched stay accepted (key
    ///   matches new-diff exactly).</item>
    ///   <item>Anything else — user edits outside any hunk, edits that
    ///   change an accepted hunk's content, edits in regions the model
    ///   never touched — gets <b>absorbed into Baseline</b> silently. No
    ///   "open" hunk surfaces for it; from the user's POV, that text is
    ///   their own code now.</item>
    /// </list>
    /// Returns true if any tracked hunks remain after the fold.
    /// </summary>
    /// <remarks>
    /// Splices into Baseline are processed in reverse OldStart order so
    /// each splice's coords stay valid in the still-being-mutated
    /// Baseline. <see cref="FileTracker.AcceptedHunks"/> entries whose
    /// <see cref="HunkKey.BaselineStart"/> sits past a splice point get
    /// shifted by <c>(NewLines.Count - OldCount)</c> so their keys still
    /// resolve against the post-fold Baseline.
    /// </remarks>
    static bool ExtendProposalLocked(FileTracker f, string newDiskContent)
    {
        // Capture which hunks were OPEN before the fold — those are the
        // only ones allowed to "absorb" user typing inside them.
        var oldHunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);
        var openOldRanges = new List<(int Start, int Count)>();
        foreach (var oh in oldHunks)
        {
            if (!f.AcceptedHunks.Contains(MakeKey(oh)))
                openOldRanges.Add((oh.OldStart, oh.OldCount));
        }

        f.LastApplied = newDiskContent;
        var newHunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);

        // Decide each new hunk: keep (model-authored, possibly extended
        // by user typing) or absorb (user-only).
        var toAbsorb = new List<LineDiff.Hunk>();
        foreach (var nh in newHunks)
        {
            var key = MakeKey(nh);
            // Identical accepted hunk passing through untouched.
            if (f.AcceptedHunks.Contains(key)) continue;
            // Open hunk extension — intersects an open old hunk's range.
            bool intersectsOpen = false;
            foreach (var (start, count) in openOldRanges)
            {
                if (RangesIntersect(nh.OldStart, nh.OldCount, start, count))
                {
                    intersectsOpen = true;
                    break;
                }
            }
            if (intersectsOpen) continue;
            // Everything else: absorb.
            toAbsorb.Add(nh);
        }

        // Splice in reverse so each splice's OldStart is still valid in
        // the partially-updated Baseline.
        toAbsorb.Sort((a, b) => b.OldStart.CompareTo(a.OldStart));
        foreach (var h in toAbsorb)
        {
            f.Baseline = LineDiff.SpliceLines(f.Baseline, h.OldStart, h.OldCount, h.NewLines);
            ShiftAcceptedHunksAfterLocked(f, h.OldStart + h.OldCount, h.NewLines.Count - h.OldCount);
        }

        // Stale accepted keys (e.g. user touched the accepted region —
        // its content changed and the new-diff hunk has a different key)
        // need pruning regardless. ApplyAcceptedHunks rebuilds the
        // Accepted blob from the (possibly shifted) Baseline + survivors.
        PruneStaleAcceptedHunksLocked(f);
        f.Accepted = ApplyAcceptedHunks(f);
        // IsAccepted ⇔ everything currently differing from baseline has
        // been explicitly accepted, with at least one accepted hunk to
        // show. After absorbing user edits this is rare but possible
        // (e.g. user deleted what they added before save).
        f.IsAccepted = string.Equals(f.Accepted, f.LastApplied, StringComparison.Ordinal)
            && f.AcceptedHunks.Count > 0;

        return f.HasProposal;
    }

    static bool RangesIntersect(int aStart, int aCount, int bStart, int bCount)
    {
        // Strict half-open interval intersection: [a, a+aCount) ∩ [b, b+bCount) ≠ ∅
        // iff aStart < bStart+bCount && bStart < aStart+aCount.
        return aStart < bStart + bCount && bStart < aStart + aCount;
    }

    // Shifts the BaselineStart of any AcceptedHunks key whose start sits
    // at or past <paramref name="boundary"/> by <paramref name="delta"/>
    // lines. Called after splicing into Baseline so existing keys remain
    // resolvable against the new Baseline coords. No-op when delta is 0
    // or there's nothing to shift.
    static void ShiftAcceptedHunksAfterLocked(FileTracker f, int boundary, int delta)
    {
        if (delta == 0 || f.AcceptedHunks.Count == 0) return;
        var shifted = new HashSet<HunkKey>();
        foreach (var k in f.AcceptedHunks)
        {
            shifted.Add(k.BaselineStart >= boundary
                ? new HunkKey(k.BaselineStart + delta, k.BaselineCount, k.NewContent)
                : k);
        }
        f.AcceptedHunks.Clear();
        foreach (var k in shifted) f.AcceptedHunks.Add(k);
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

                // External modification. Three concerns to handle in order:
                //
                //   (a) Drop denial records whose post-deny expected state
                //       doesn't match the new disk content (the surviving-
                //       denials invariant).
                var before = f.Denied.Count;
                var removed = f.Denied.RemoveAll(
                    d => !string.Equals(d.DiskContentAtDeny, content, StringComparison.Ordinal));

                //   (b) Partition-and-absorb. Hunks the user introduced
                //       outside any open hunk (or by touching an accepted
                //       hunk) are silently absorbed into Baseline so they
                //       don't surface as "open" hunks the user has to
                //       interact with. Only OPEN model hunks may extend
                //       to swallow user edits inside them. Accepted hunks
                //       that the user didn't touch keep their key and
                //       their marker.
                //
                //       Rules per the simplified model:
                //         • Only models create new hunks.
                //         • Users may extend an open hunk by typing inside.
                //         • Touching an accepted hunk drops its marker
                //           and absorbs the region into Baseline (handled
                //           via key-mismatch + absorb in this partition).
                //         • All other user edits absorb silently — never
                //           a new "open" hunk that wasn't authored by the
                //           model.
                bool extendedProposal = ExtendProposalLocked(f, content);
                if (extendedProposal)
                {
                    ExtensionLogger.Info("changes",
                        $"External edit on {absolutePath}: extended proposal "
                        + $"(dropped {removed}/{before} denial(s), {f.AcceptedHunks.Count} accepted hunk(s) survive)");
                }
                else
                {
                    ExtensionLogger.Info("changes",
                        $"External edit on {absolutePath}: dropped {removed}/{before} denial(s)");
                }

                //   (c) If we didn't extend a proposal AND nothing
                //       meaningful is left, drop the entry entirely so the
                //       next model touch captures a fresh Baseline
                //       reflecting the user's intermediate state.
                bool entryDropped = false;
                if (!extendedProposal && !f.HasProposal && f.Denied.Count == 0)
                {
                    session.Files.Remove(key);
                    entryDropped = true;
                    ExtensionLogger.Info("changes",
                        $"Dropped tracker entry for {absolutePath} (clean state after external edit)");
                }

                if (removed > 0 || entryDropped || extendedProposal) changedSessions.Add(kv.Key);
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
