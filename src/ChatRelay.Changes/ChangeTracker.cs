using System.Collections.Concurrent;
using ChatRelay.Host;
using ChatRelay.Logging;

namespace ChatRelay.Changes;

/// <summary>In-memory per-session change tracker. Closing VS terminates the host and wipes everything.</summary>
public sealed class ChangeTracker
{
    readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    /// <summary>Fired after any state mutation so the host can emit onChangesUpdated. Set by HostService.</summary>
    public Action<string, SessionChangesSnapshot>? Notify { get; set; }

    /// <summary>Workspace root for path filtering and display paths. Null = nothing tracked. Reassigning rebuilds the watcher.</summary>
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

    /// <summary>Tests turn this off before assigning WorkspaceRoot so the watcher doesn't race their synchronous writes.</summary>
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
        try { _watcher = new WorkspaceWatcher(_workspaceRoot!, OnExternalFileChange); }
        catch (Exception ex) { ExtensionLogger.Warn("changes", $"Failed to start workspace watcher: {ex.Message}"); }
    }

    public SessionChangesSnapshot Snapshot(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync) return BuildSnapshotLocked(sessionId, s);
    }

    /// <summary>Stamps the session's current model name so subsequent tool_use observations pin it onto the FileTracker.</summary>
    public void SetCurrentModel(string sessionId, string? modelDisplayName)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync) s.CurrentModel = string.IsNullOrEmpty(modelDisplayName) ? null : modelDisplayName;
    }

    /// <summary>Filters tool-use observations to file-mutating tools inside the workspace, then advances per-file state.</summary>
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
                case ToolCallPhase.Requested: EnsureBaselineLocked(s, absolute); break;
                case ToolCallPhase.Completed: UpdateLastAppliedLocked(s, absolute); break;
            }
        }
        FireNotify(sessionId);
    }

    /// <summary>Whole-file accept: Baseline absorbs LastApplied. Future diffs and Deny revert to here.</summary>
    public bool Accept(string sessionId, string filePath)
    {
        var s = GetOrCreate(sessionId);
        bool changed;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            if (!f.HasProposal) return false;
            var counts = LineDiff.Compute(f.Baseline, f.LastApplied);
            f.AcceptedLinesAdded += counts.Added;
            f.AcceptedLinesRemoved += counts.Removed;
            s.AcceptedLinesAdded += counts.Added;
            s.AcceptedLinesRemoved += counts.Removed;
            f.Baseline = f.LastApplied;
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    /// <summary>Per-hunk accept: folds the hunk's NewLines into Baseline at its baseline coords. Other open hunks stay visible.</summary>
    public bool AcceptHunk(string sessionId, string filePath, int baselineStart, int baselineCount)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            var hunk = FindHunkByCoords(LineDiff.ComputeHunks(f.Baseline, f.LastApplied), baselineStart, baselineCount);
            if (hunk is null) return false;
            // Re-diff just this hunk's lines so noise context coalesced into it doesn't inflate the cumulative counts.
            var counts = LineDiff.Compute(string.Join("\n", hunk.Value.OldLines), string.Join("\n", hunk.Value.NewLines));
            f.AcceptedLinesAdded += counts.Added;
            f.AcceptedLinesRemoved += counts.Removed;
            s.AcceptedLinesAdded += counts.Added;
            s.AcceptedLinesRemoved += counts.Removed;
            f.Baseline = LineDiff.SpliceLines(f.Baseline, baselineStart, baselineCount, hunk.Value.NewLines);
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    /// <summary>Per-hunk reject: writes Baseline content over the hunk's region in LastApplied + on disk, stashes a redo entry.</summary>
    public bool RejectHunk(string sessionId, string filePath, int baselineStart, int baselineCount)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            var hunkOpt = FindHunkByCoords(LineDiff.ComputeHunks(f.Baseline, f.LastApplied), baselineStart, baselineCount);
            if (hunkOpt is null) return false;
            var hunk = hunkOpt.Value;

            var preReject = f.LastApplied;
            var newLastApplied = LineDiff.SpliceLines(f.LastApplied, hunk.NewStart, hunk.NewCount, hunk.OldLines);
            try { File.WriteAllText(f.AbsolutePath, newLastApplied); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Hunk reject write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            f.LastApplied = newLastApplied;
            f.Denied.Add(new DeniedChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DeniedAt = DateTime.UtcNow,
                ContentToReapply = preReject,
                DiskContentAtDeny = newLastApplied,
                LinesAdded = hunk.NewCount,
                LinesRemoved = hunk.OldCount,
            });
            changed = true;
        }
        if (changed) FireNotify(sessionId);
        return changed;
    }

    /// <summary>No-op under fold-on-accept (accepted hunks are folded into Baseline immediately). Kept on the wire for compatibility.</summary>
    public bool InvalidateAcceptedHunk(string sessionId, string filePath, int baselineStart, int baselineCount) => false;

    static LineDiff.Hunk? FindHunkByCoords(IReadOnlyList<LineDiff.Hunk> hunks, int baselineStart, int baselineCount)
    {
        for (int i = 0; i < hunks.Count; i++)
            if (hunks[i].OldStart == baselineStart && hunks[i].OldCount == baselineCount)
                return hunks[i];
        return null;
    }

    /// <summary>Whole-file deny: writes Baseline back to disk, stashes a redo entry.</summary>
    public bool Deny(string sessionId, string filePath)
    {
        var s = GetOrCreate(sessionId);
        bool changed = false;
        lock (s.Sync)
        {
            if (!s.Files.TryGetValue(NormalisePath(filePath), out var f)) return false;
            if (!f.HasProposal) return false;

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

            try { File.WriteAllText(f.AbsolutePath, f.Baseline); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("changes", $"Deny write failed for {f.AbsolutePath}: {ex.Message}");
                return false;
            }

            f.LastApplied = f.Baseline;
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

            string current;
            try { current = File.ReadAllText(f.AbsolutePath); }
            catch { return false; }

            if (!string.Equals(current, record.DiskContentAtDeny, StringComparison.Ordinal))
            {
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
                f.LastApplied = record.ContentToReapply;
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
                if (!f.HasProposal) continue;
                var counts = LineDiff.Compute(f.Baseline, f.LastApplied);
                f.AcceptedLinesAdded += counts.Added;
                f.AcceptedLinesRemoved += counts.Removed;
                s.AcceptedLinesAdded += counts.Added;
                s.AcceptedLinesRemoved += counts.Removed;
                f.Baseline = f.LastApplied;
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
                if (!f.HasProposal) continue;
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
                try { File.WriteAllText(f.AbsolutePath, f.Baseline); }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("changes", $"Bulk deny write failed for {f.AbsolutePath}: {ex.Message}");
                    continue;
                }
                f.LastApplied = f.Baseline;
                f.Denied.Add(record);
                any = true;
            }
        }
        if (any) FireNotify(sessionId);
        return any;
    }

    /// <summary>Folds an external user edit: open hunks may grow to swallow user typing inside them; everything else absorbs into Baseline silently.</summary>
    static bool ExtendProposalLocked(FileTracker f, string newDiskContent)
    {
        var openOldRanges = new List<(int Start, int Count)>();
        foreach (var oh in LineDiff.ComputeHunks(f.Baseline, f.LastApplied))
            openOldRanges.Add((oh.OldStart, oh.OldCount));

        f.LastApplied = newDiskContent;
        var newHunks = LineDiff.ComputeHunks(f.Baseline, f.LastApplied);

        var toAbsorb = new List<LineDiff.Hunk>();
        foreach (var nh in newHunks)
        {
            bool intersectsOpen = false;
            foreach (var (start, count) in openOldRanges)
            {
                if (RangesIntersect(nh.OldStart, nh.OldCount, start, count)) { intersectsOpen = true; break; }
            }
            if (!intersectsOpen) toAbsorb.Add(nh);
        }

        // Splice in reverse so each splice's OldStart stays valid in the still-mutating Baseline.
        toAbsorb.Sort((a, b) => b.OldStart.CompareTo(a.OldStart));
        foreach (var h in toAbsorb)
            f.Baseline = LineDiff.SpliceLines(f.Baseline, h.OldStart, h.OldCount, h.NewLines);

        return f.HasProposal;
    }

    /// <summary>Strict half-open interval intersection: [a, a+aCount) ∩ [b, b+bCount) ≠ ∅.</summary>
    static bool RangesIntersect(int aStart, int aCount, int bStart, int bCount) =>
        aStart < bStart + bCount && bStart < aStart + aCount;

    public int CountOpen(string sessionId)
    {
        var s = GetOrCreate(sessionId);
        lock (s.Sync) return s.Files.Values.Count(f => f.HasProposal);
    }

    public void ClearSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _)) FireNotify(sessionId);
    }

    // -- Internals -------------------------------------------------------

    SessionState GetOrCreate(string sessionId) => _sessions.GetOrAdd(sessionId, _ => new SessionState());

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
            // Disk drifted between turns (user edited and the watcher hasn't caught up): take a fresh checkpoint.
            if (!existing.ExpectingWrite)
            {
                string? current = TryRead(absolute);
                if (current is not null
                    && !string.Equals(current, existing.LastApplied, StringComparison.Ordinal)
                    && !string.Equals(current, existing.Baseline, StringComparison.Ordinal))
                {
                    ExtensionLogger.Info("changes", $"Refreshing Baseline for {absolute} — disk drifted before tool_use");
                    existing.Baseline = current;
                    existing.LastApplied = current;
                    existing.Denied.Clear();
                }
            }
            existing.ExpectingWrite = true;
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
            LastApplied = baseline,
            ExpectingWrite = true,
            LastModel = s.CurrentModel,
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
            // tool_result with no prior tool_use — race or unknown tool. Treat current disk as both blobs.
            EnsureBaselineLocked(s, absolute);
            if (s.Files.TryGetValue(key, out f)) f.ExpectingWrite = false;
            return;
        }
        try { f.LastApplied = File.Exists(absolute) ? File.ReadAllText(absolute) : string.Empty; }
        catch (Exception ex) { ExtensionLogger.Warn("changes", $"Post-write read failed for {absolute}: {ex.Message}"); }
        f.ExpectingWrite = false;
        f.Denied.RemoveAll(d => !string.Equals(d.DiskContentAtDeny, f.LastApplied, StringComparison.Ordinal));
    }

    /// <summary>Workspace watcher entry. Drops stale denials, folds external edits via ExtendProposalLocked.</summary>
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

                if (f.ExpectingWrite)
                {
                    ExtensionLogger.Debug("changes", $"Watcher echo (ExpectingWrite): {absolutePath}");
                    continue;
                }

                // Read disk inside the lock so a concurrent Accept/Deny/Redo can't settle state between read and check.
                string? content = TryReadWithRetry(absolutePath);
                if (content is null) continue;

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

                var before = f.Denied.Count;
                var removed = f.Denied.RemoveAll(d => !string.Equals(d.DiskContentAtDeny, content, StringComparison.Ordinal));

                bool extendedProposal = ExtendProposalLocked(f, content);
                ExtensionLogger.Info("changes",
                    $"External edit on {absolutePath}: dropped {removed}/{before} denial(s)"
                    + (extendedProposal ? " (extended proposal)" : ""));

                bool entryDropped = false;
                if (!extendedProposal && !f.HasProposal && f.Denied.Count == 0 && !f.HasAcceptedHistory)
                {
                    session.Files.Remove(key);
                    entryDropped = true;
                    ExtensionLogger.Info("changes", $"Dropped tracker entry for {absolutePath} (clean state after external edit)");
                }

                if (removed > 0 || entryDropped || extendedProposal) changedSessions.Add(kv.Key);
            }
        }
        if (!anyTracked) ExtensionLogger.Debug("changes", $"Watcher fired for untracked path: {absolutePath}");
        foreach (var sid in changedSessions) FireNotify(sid);
    }

    /// <summary>Up to 5 × 50ms attempts to ride out the writer's handle release. Returns null if every attempt fails.</summary>
    static string? TryReadWithRetry(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(50); }
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
            // Emit a row whenever there's open work OR a non-zero accepted history to display.
            if (f.HasProposal || f.HasAcceptedHistory)
            {
                var counts = f.HasProposal ? LineDiff.Compute(f.Baseline, f.LastApplied) : default;
                var hunks = f.HasProposal ? LineDiff.ComputeHunks(f.Baseline, f.LastApplied) : (IReadOnlyList<LineDiff.Hunk>)Array.Empty<LineDiff.Hunk>();
                var hunkInfos = new List<HunkInfo>(hunks.Count);
                foreach (var h in hunks)
                {
                    hunkInfos.Add(new HunkInfo(
                        BaselineStart: h.OldStart,
                        BaselineCount: h.OldCount,
                        CurrentStart: h.NewStart,
                        CurrentCount: h.NewCount,
                        BaselineLines: h.OldLines,
                        CurrentLines: h.NewLines,
                        State: "open",
                        Model: f.LastModel));
                }
                proposals.Add(new ChangeProposal(
                    FilePath: f.DisplayPath,
                    AbsolutePath: f.AbsolutePath,
                    LinesAdded: counts.Added,
                    LinesRemoved: counts.Removed,
                    State: "open",
                    Hunks: hunkInfos,
                    AcceptedLinesAdded: f.AcceptedLinesAdded,
                    AcceptedLinesRemoved: f.AcceptedLinesRemoved));
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
                            CanRedo: true))
                        .ToList()));
            }
        }

        return new SessionChangesSnapshot(sessionId, proposals, denials, s.AcceptedLinesAdded, s.AcceptedLinesRemoved);
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
        else snap = new SessionChangesSnapshot(sessionId, [], []);
        try { notify(sessionId, snap); }
        catch (Exception ex) { ExtensionLogger.Warn("changes", "Notify threw: " + ex.Message); }
    }

    sealed class SessionState
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, FileTracker> Files = new(StringComparer.OrdinalIgnoreCase);
        public string? CurrentModel;
        public int AcceptedLinesAdded;
        public int AcceptedLinesRemoved;
    }
}
