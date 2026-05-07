using System.Text.Json;
using ChatRelay.Changes;
using ChatRelay.Host;

namespace ChatRelay.IntegrationTests;

/// <summary>
/// Tests for the Phase 4.1 per-hunk surface — <see cref="LineDiff.ComputeHunks"/>
/// and the matching <c>AcceptHunk</c> / <c>RejectHunk</c> tracker operations.
/// Drives <see cref="ChangeTracker"/> directly the same way
/// <c>ChangeTrackerTests</c> does — temp workspace, simulated tool_use /
/// tool_result, real disk writes.
/// </summary>
public sealed class HunkOperationsTests : IDisposable
{
    readonly string _workspace;
    readonly ChangeTracker _tracker;
    const string SessionId = "hunk-test-session";

    public HunkOperationsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ChatRelayHunkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        // EnableFileSystemWatcher MUST be set before WorkspaceRoot — the
        // workspace setter is what triggers RebuildWatcher. Tests call
        // OnExternalFileChange directly; leaving the watcher running
        // would race against the test's synchronous WriteWithRetry,
        // catching partial-disk reads and corrupting tracker state.
        _tracker = new ChangeTracker
        {
            EnableFileSystemWatcher = false,
            WorkspaceRoot = _workspace,
        };
    }

    public void Dispose()
    {
        _tracker.WorkspaceRoot = null;
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // After fold-on-accept the row stays alive carrying accepted history, so "no work to do" means "every row has 0 open lines".
    static void AssertNoOpenChanges(SessionChangesSnapshot snap) =>
        Assert.All(snap.Proposals, p => Assert.True(p.LinesAdded == 0 && p.LinesRemoved == 0));

    // ---- LineDiff.ComputeHunks --------------------------------------

    [Fact]
    public void ComputeHunks_pure_insertion_at_top()
    {
        var hunks = LineDiff.ComputeHunks("b\nc\n", "a\nb\nc\n");
        var h = Assert.Single(hunks);
        Assert.Equal(0, h.OldStart);
        Assert.Equal(0, h.OldCount);
        Assert.Equal(0, h.NewStart);
        Assert.Equal(1, h.NewCount);
        Assert.Equal(new[] { "a" }, h.NewLines);
    }

    [Fact]
    public void ComputeHunks_pure_deletion_in_middle()
    {
        var hunks = LineDiff.ComputeHunks("a\nb\nc\nd\n", "a\nd\n");
        var h = Assert.Single(hunks);
        Assert.Equal(1, h.OldStart);
        Assert.Equal(2, h.OldCount);
        Assert.Equal(0, h.NewCount);
        Assert.Equal(new[] { "b", "c" }, h.OldLines);
    }

    [Fact]
    public void ComputeHunks_replacement()
    {
        var hunks = LineDiff.ComputeHunks("a\nb\nc\n", "a\nB\nc\n");
        var h = Assert.Single(hunks);
        Assert.Equal(1, h.OldStart);
        Assert.Equal(1, h.OldCount);
        Assert.Equal(1, h.NewStart);
        Assert.Equal(1, h.NewCount);
        Assert.Equal(new[] { "b" }, h.OldLines);
        Assert.Equal(new[] { "B" }, h.NewLines);
    }

    [Fact]
    public void ComputeHunks_substantive_gap_splits_into_two_hunks()
    {
        // Real preserved code between two edits — keep them separate so
        // the user sees two distinct change regions.
        var hunks = LineDiff.ComputeHunks(
            "a\nkeep1\nkeep2\nf\n",
            "A\nkeep1\nkeep2\nF\n");
        Assert.Equal(2, hunks.Count);
        Assert.Equal(0, hunks[0].OldStart);
        Assert.Equal(3, hunks[1].OldStart);
    }

    [Fact]
    public void ComputeHunks_whitespace_only_gap_coalesces()
    {
        // Two edits separated only by blank lines — the diff aligned them
        // by accident and we don't want to fragment the model's logical
        // change around them. Result: one hunk spanning the whole region.
        var hunks = LineDiff.ComputeHunks(
            "a\n\n\nd\n",
            "A\n\n\nD\n");
        var h = Assert.Single(hunks);
        Assert.Equal(0, h.OldStart);
        Assert.Equal(4, h.OldCount);
        Assert.Equal(new[] { "a", "", "", "d" }, h.OldLines);
        Assert.Equal(new[] { "A", "", "", "D" }, h.NewLines);
    }

    [Fact]
    public void ComputeHunks_short_substantive_gap_does_not_coalesce()
    {
        // A single line of real code between two edits is enough to keep
        // them as distinct hunks — we don't merge across substantive
        // content no matter how short the gap.
        var hunks = LineDiff.ComputeHunks(
            "a\nkeep\nc\n",
            "A\nkeep\nC\n");
        Assert.Equal(2, hunks.Count);
    }

    [Fact]
    public void ComputeHunks_brace_only_gap_coalesces_method_replacement()
    {
        // Replacing a method's signature and body: DiffPlex aligns the
        // matched `{` and `}` between the two changed regions. Those
        // are structural noise, not preserved code — coalesce into one
        // hunk so the user sees a single replacement.
        var hunks = LineDiff.ComputeHunks(
            "public void Foo()\n{\n    DoOldThing();\n}\n",
            "public void Bar()\n{\n    DoNewThing();\n}\n");
        var h = Assert.Single(hunks);
        Assert.Contains("public void Foo()", h.OldLines);
        Assert.Contains("    DoOldThing();", h.OldLines);
        Assert.Contains("public void Bar()", h.NewLines);
        Assert.Contains("    DoNewThing();", h.NewLines);
    }

    [Fact]
    public void ComputeHunks_identical_returns_empty()
    {
        Assert.Empty(LineDiff.ComputeHunks("a\nb\nc\n", "a\nb\nc\n"));
    }

    // ---- LineDiff.SpliceLines ---------------------------------------

    [Fact]
    public void SpliceLines_replace_preserves_LF_style()
    {
        var result = LineDiff.SpliceLines("a\nb\nc\n", 1, 1, new[] { "B" });
        Assert.Equal("a\nB\nc\n", result);
    }

    [Fact]
    public void SpliceLines_replace_preserves_CRLF_style()
    {
        var result = LineDiff.SpliceLines("a\r\nb\r\nc\r\n", 1, 1, new[] { "B" });
        Assert.Equal("a\r\nB\r\nc\r\n", result);
    }

    [Fact]
    public void SpliceLines_pure_insert_no_trailing_newline()
    {
        // Insert at end of a file that doesn't end with a newline; we have
        // to add a separator newline before the new content.
        var result = LineDiff.SpliceLines("a\nb", 2, 0, new[] { "c" });
        Assert.Equal("a\nb\nc", result);
    }

    [Fact]
    public void SpliceLines_pure_delete_in_middle()
    {
        var result = LineDiff.SpliceLines("a\nb\nc\nd\n", 1, 2, Array.Empty<string>());
        Assert.Equal("a\nd\n", result);
    }

    // ---- ChangeTracker.AcceptHunk / RejectHunk ----------------------

    string MakeFile(string name, string content)
    {
        var path = Path.Combine(_workspace, name);
        WriteWithRetry(path, content);
        return path;
    }

    static void WriteWithRetry(string path, string content)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.WriteAllText(path, content); return; }
            catch (IOException) when (attempt < 9) { Thread.Sleep(20); }
        }
        File.WriteAllText(path, content);
    }

    static ToolCallObservation Edit(string filePath, ToolCallPhase phase) => new()
    {
        ToolName = "Edit",
        InputJson = JsonSerializer.Serialize(new { file_path = filePath }),
        Phase = phase,
    };

    void EditCycle(string path, string newContent)
    {
        _tracker.Observe(SessionId, Edit(path, ToolCallPhase.Requested));
        WriteWithRetry(path, newContent);
        _tracker.Observe(SessionId, Edit(path, ToolCallPhase.Completed));
    }

    [Fact]
    public void AcceptHunk_folds_first_of_two_into_baseline_leaves_second_open()
    {
        // Gap of 4 unchanged lines (b,c,d,e) keeps the two edits apart;
        // smaller gaps would coalesce per LineDiff's noise rule.
        var path = MakeFile("multi.cs", "a\nb\nc\nd\ne\nf\ng\n");
        EditCycle(path, "A\nb\nc\nd\ne\nF\ng\n");      // two hunks: replace 'a' and 'f'

        var snap = _tracker.Snapshot(SessionId);
        var p = Assert.Single(snap.Proposals);
        Assert.Equal(2, p.Hunks.Count);
        Assert.All(p.Hunks, h => Assert.Equal("open", h.State));

        // Accept the first hunk only — folds it into Baseline.
        var first = p.Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, first.BaselineStart, first.BaselineCount));

        var snap2 = _tracker.Snapshot(SessionId);
        var p2 = Assert.Single(snap2.Proposals);
        // Only the second hunk remains visible; the first is now part of
        // the new Baseline (the truth) and no longer surfaces as a hunk.
        var remaining = Assert.Single(p2.Hunks);
        Assert.Equal("open", remaining.State);

        // Disk untouched — Accept doesn't write.
        Assert.Equal("A\nb\nc\nd\ne\nF\ng\n", File.ReadAllText(path));
    }

    [Fact]
    public void RejectHunk_open_reverts_only_that_region_and_keeps_others()
    {
        var path = MakeFile("multi.cs", "a\nb\nc\nd\ne\nf\ng\n");
        EditCycle(path, "A\nb\nc\nd\ne\nF\ng\n");

        var p = _tracker.Snapshot(SessionId).Proposals[0];
        // Reject only the second hunk (replacement of 'f').
        var second = p.Hunks[1];
        Assert.True(_tracker.RejectHunk(SessionId, path, second.BaselineStart, second.BaselineCount));

        // First model edit ('a' → 'A') survives, second reverted ('F' → 'f').
        Assert.Equal("A\nb\nc\nd\ne\nf\ng\n", File.ReadAllText(path));

        var snap = _tracker.Snapshot(SessionId);
        var p2 = Assert.Single(snap.Proposals);
        var hunk = Assert.Single(p2.Hunks);
        Assert.Equal(0, hunk.BaselineStart);
        Assert.Single(snap.Denials);
    }

    [Fact]
    public void RejectHunk_after_accept_finds_no_hunk_and_does_not_revert()
    {
        // Accept folds the hunk into Baseline, so the same coordinates no
        // longer match any hunk in the live diff. RejectHunk returns
        // false (nothing to do), disk stays at the accepted content.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");

        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        Assert.False(_tracker.RejectHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // Disk untouched, no denial recorded, no proposal remains.
        Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
        var snap = _tracker.Snapshot(SessionId);
        AssertNoOpenChanges(snap);
        Assert.Empty(snap.Denials);
    }

    [Fact]
    public void RejectHunk_after_AcceptAllOpen_finds_nothing_to_reject()
    {
        // AcceptAllOpen folds every open hunk into Baseline, so per-hunk
        // reject on the (now-baseline) coordinates is a no-op.
        var path = MakeFile("multi.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nC\n");
        var openHunks = _tracker.Snapshot(SessionId).Proposals[0].Hunks;
        Assert.True(_tracker.AcceptAllOpen(SessionId));

        foreach (var h in openHunks)
            Assert.False(_tracker.RejectHunk(SessionId, path, h.BaselineStart, h.BaselineCount));

        // Disk has the accepted edits; no proposal remains.
        Assert.Equal("A\nb\nC\n", File.ReadAllText(path));
        AssertNoOpenChanges(_tracker.Snapshot(SessionId));
    }

    [Fact]
    public void Re_edit_after_accept_diffs_against_the_new_baseline()
    {
        // Accept folds the prior change into Baseline, so a follow-up
        // model edit diffs against the post-accept truth. New content
        // shows as a fresh open hunk — there's no stale "accepted
        // marker" to carry forward because there are no hunks left
        // after acceptance.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nc\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));
        AssertNoOpenChanges(_tracker.Snapshot(SessionId));

        // Model edits the same line again with different content.
        EditCycle(path, "X\nb\nc\n");

        var newHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.Equal(0, newHunk.BaselineStart);
        Assert.Equal("open", newHunk.State);
    }

    [Fact]
    public void Re_edit_after_accept_with_same_content_produces_no_proposal()
    {
        // If the model regenerates the same content on a subsequent turn,
        // disk equals the post-accept Baseline → no diff, no proposal,
        // no UI churn for the user.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nc\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // Same content as before — the model "rewrote" the file to an
        // identical end state.
        EditCycle(path, "A\nb\nc\n");

        AssertNoOpenChanges(_tracker.Snapshot(SessionId));
    }

    // ---- Phase 4.4a: live-buffer extension --------------------------
    // External-edit flow: the user manually edits a file that already
    // has an open / accepted proposal. The watcher fires on save and
    // the tracker folds those edits into the live diff so accept/reject
    // covers the user's lines too. PhaseSpec covered:
    //   • Open hunk grows with user-added lines
    //   • Reject of the extended hunk removes user lines along with model lines
    //   • External edit to an accepted line drops that hunk's marker
    //   • External edit outside any hunk surfaces as a new open hunk
    //   • External edit inside an accepted hunk that doesn't change its
    //     content (e.g. inserting blank lines after) keeps the accept
    //     marker for the model's lines

    [Fact]
    public void External_edit_extends_open_hunk_to_include_user_added_lines()
    {
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");        // model: replace b with B

        // User adds a line manually within the hunk and saves.
        WriteWithRetry(path, "a\nB\nX\nc\n");
        _tracker.OnExternalFileChange(path);

        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        var h = Assert.Single(p.Hunks);
        Assert.Equal("open", h.State);
        // The hunk now covers the model's replacement AND the user's insert.
        Assert.Contains("X", h.CurrentLines);
        Assert.Contains("B", h.CurrentLines);
    }

    [Fact]
    public void External_edit_to_accepted_line_absorbs_into_baseline_and_marker_disappears()
    {
        // Under "users only remove": touching an accepted line drops the
        // marker AND removes the hunk from memory entirely. Baseline
        // absorbs the new content; the file no longer has a proposal for
        // that region.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // User modifies the model's accepted line and saves.
        WriteWithRetry(path, "a\nBx\nc\n");
        _tracker.OnExternalFileChange(path);

        // No proposal — Baseline absorbed "Bx", LastApplied matches.
        AssertNoOpenChanges(_tracker.Snapshot(SessionId));
        Assert.Equal("a\nBx\nc\n", File.ReadAllText(path));
    }

    [Fact]
    public void External_edit_outside_hunk_absorbs_silently_into_baseline()
    {
        // Under the new model, edits outside any model-authored hunk
        // never surface as new "open" hunks — they absorb into Baseline
        // silently. The original model hunk stays unchanged.
        var path = MakeFile("foo.cs", "a\nb\nc\nd\n");
        EditCycle(path, "a\nB\nc\nd\n");    // model: replace b with B

        // User edits a region the model didn't touch.
        WriteWithRetry(path, "a\nB\nc\nD\n");
        _tracker.OnExternalFileChange(path);

        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        // Only the original model hunk — the user's "D" was absorbed.
        var h = Assert.Single(p.Hunks);
        Assert.Equal("open", h.State);
        Assert.Contains("B", h.CurrentLines);
    }

    [Fact]
    public void External_edit_outside_an_accepted_region_absorbs_silently()
    {
        // After accept the model's change is part of Baseline; a later
        // user edit elsewhere has no open hunks to extend and no accepted
        // marker to preserve, so it absorbs into Baseline. End state:
        // disk and Baseline match, no proposal.
        var path = MakeFile("foo.cs", "a\nb\nc\nd\ne\nf\n");
        EditCycle(path, "a\nb\nc\nd\ne\nF\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // User types "X" at the start, far from the accepted "F" line.
        WriteWithRetry(path, "X\na\nb\nc\nd\ne\nF\n");
        _tracker.OnExternalFileChange(path);

        AssertNoOpenChanges(_tracker.Snapshot(SessionId));
        Assert.Equal("X\na\nb\nc\nd\ne\nF\n", File.ReadAllText(path));
    }

    [Fact]
    public void Reject_after_user_extends_open_hunk_reverts_user_lines_too()
    {
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");

        // User adds a line in the hunk, then saves.
        WriteWithRetry(path, "a\nB\nX\nc\n");
        _tracker.OnExternalFileChange(path);

        var h = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.RejectHunk(SessionId, path, h.BaselineStart, h.BaselineCount));

        // Reject reverts the entire (extended) region to baseline — both
        // the model's "B" and the user's "X" are gone.
        Assert.Equal("a\nb\nc\n", File.ReadAllText(path));
    }

    [Fact]
    public void Accept_after_user_extends_open_hunk_keeps_user_lines()
    {
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");

        WriteWithRetry(path, "a\nB\nX\nc\n");
        _tracker.OnExternalFileChange(path);

        var h = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, h.BaselineStart, h.BaselineCount));

        // Accept folds the extended hunk into Baseline. Disk untouched
        // (still has both the model's "B" and user's "X"), no proposal
        // remains because the new Baseline matches disk.
        Assert.Equal("a\nB\nX\nc\n", File.ReadAllText(path));
        AssertNoOpenChanges(_tracker.Snapshot(SessionId));
    }

    [Fact]
    public void InvalidateAcceptedHunk_is_noop_under_fold_on_accept_model()
    {
        // Accept folds the hunk into Baseline immediately, so by the time
        // the editor would send InvalidateAcceptedHunk the hunk no longer
        // exists in the live diff and the RPC returns false. Kept on the
        // wire for compatibility but the editor doesn't need to call it
        // anymore — accepts are self-finalizing.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        Assert.False(_tracker.InvalidateAcceptedHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));
        Assert.False(_tracker.InvalidateAcceptedHunk(SessionId, path, 99, 99));
    }

    [Fact]
    public void External_edit_with_no_proposal_still_drops_tracker_entry()
    {
        // Ensure we didn't break the Phase 3.1 case: an external edit on
        // a file with no live proposal (Baseline == LastApplied) and no
        // denials still drops the tracker entry so the next model edit
        // captures a fresh baseline.
        var path = MakeFile("foo.cs", "v1");
        EditCycle(path, "v2");
        _tracker.Deny(SessionId, path);    // back to v1, one denial

        // User edits to a third state, dropping the denial.
        WriteWithRetry(path, "v3-user");
        _tracker.OnExternalFileChange(path);

        // Next model edit should baseline against v3-user, not v1.
        EditCycle(path, "v4-model");
        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        Assert.Equal(1, p.LinesAdded);
        Assert.Equal(1, p.LinesRemoved);
    }
}
