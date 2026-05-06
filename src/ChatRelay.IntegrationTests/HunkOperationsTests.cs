using System.Text.Json;
using ChatRelay.Changes;

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
        _tracker = new ChangeTracker { WorkspaceRoot = _workspace };
    }

    public void Dispose()
    {
        _tracker.WorkspaceRoot = null;
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

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
    public void ComputeHunks_two_separate_changes_become_two_hunks()
    {
        var hunks = LineDiff.ComputeHunks(
            "a\nb\nc\nd\ne\n",
            "A\nb\nc\nD\ne\n");
        Assert.Equal(2, hunks.Count);
        Assert.Equal(0, hunks[0].OldStart);
        Assert.Equal(3, hunks[1].OldStart);
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
    public void AcceptHunk_locks_in_one_of_two_open_hunks()
    {
        var path = MakeFile("multi.cs", "a\nb\nc\nd\ne\n");
        EditCycle(path, "A\nb\nc\nD\ne\n");           // two hunks: replace 'a' and 'd'

        var snap = _tracker.Snapshot(SessionId);
        var p = Assert.Single(snap.Proposals);
        Assert.Equal(2, p.Hunks.Count);
        Assert.All(p.Hunks, h => Assert.Equal("open", h.State));

        // Accept the first hunk only.
        var first = p.Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, first.BaselineStart, first.BaselineCount));

        var snap2 = _tracker.Snapshot(SessionId);
        var p2 = Assert.Single(snap2.Proposals);
        // First hunk now accepted, second still open. File is partly accepted —
        // overall State stays "open" until everything's locked in.
        Assert.Equal("open", p2.State);
        Assert.Equal("accepted", p2.Hunks[0].State);
        Assert.Equal("open", p2.Hunks[1].State);

        // Disk untouched — Accept doesn't write.
        Assert.Equal("A\nb\nc\nD\ne\n", File.ReadAllText(path));
    }

    [Fact]
    public void RejectHunk_open_reverts_only_that_region_and_keeps_others()
    {
        var path = MakeFile("multi.cs", "a\nb\nc\nd\ne\n");
        EditCycle(path, "A\nb\nc\nD\ne\n");

        var p = _tracker.Snapshot(SessionId).Proposals[0];
        // Reject only the second hunk (replacement of 'd').
        var second = p.Hunks[1];
        Assert.True(_tracker.RejectHunk(SessionId, path, second.BaselineStart, second.BaselineCount));

        // First model edit ('a' → 'A') survives, second reverted ('D' → 'd').
        Assert.Equal("A\nb\nc\nd\ne\n", File.ReadAllText(path));

        var snap = _tracker.Snapshot(SessionId);
        var p2 = Assert.Single(snap.Proposals);
        var hunk = Assert.Single(p2.Hunks);
        Assert.Equal(0, hunk.BaselineStart);
        Assert.Single(snap.Denials);
    }

    [Fact]
    public void RejectHunk_accepted_reverts_to_baseline_in_both_blobs()
    {
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "a\nB\nc\n");

        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // Reject the same (now-accepted) hunk.
        Assert.True(_tracker.RejectHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        Assert.Equal("a\nb\nc\n", File.ReadAllText(path));
        var snap = _tracker.Snapshot(SessionId);
        Assert.Empty(snap.Proposals);                 // file is fully back at baseline
        Assert.Single(snap.Denials);
    }

    [Fact]
    public void AcceptAllOpen_populates_AcceptedHunks_so_per_hunk_reject_keeps_working()
    {
        // Whole-file accept must set up AcceptedHunks consistently — otherwise
        // a follow-up per-hunk reject couldn't know to revert the hunk's
        // contribution in Accepted (would only revert LastApplied / disk).
        var path = MakeFile("multi.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nC\n");
        Assert.True(_tracker.AcceptAllOpen(SessionId));

        var p = _tracker.Snapshot(SessionId).Proposals[0];
        Assert.Equal("accepted", p.State);
        Assert.All(p.Hunks, h => Assert.Equal("accepted", h.State));

        // Reject one hunk. File should mix: rejected hunk reverts, other stays.
        var first = p.Hunks[0];
        Assert.True(_tracker.RejectHunk(SessionId, path, first.BaselineStart, first.BaselineCount));

        Assert.Equal("a\nb\nC\n", File.ReadAllText(path));
        var p2 = _tracker.Snapshot(SessionId).Proposals[0];
        // One hunk left, accepted (the second one was accepted via AcceptAllOpen
        // and isn't touched by per-hunk reject of the first).
        var remaining = Assert.Single(p2.Hunks);
        Assert.Equal("accepted", remaining.State);
    }

    [Fact]
    public void Re_edit_at_same_baseline_coords_with_different_content_returns_to_open()
    {
        // Regression for the carry-forward bug: previously HunkKey was
        // (BaselineStart, BaselineCount) only, so a follow-up edit at the
        // same coords inherited the prior accept-marker. Now the key
        // includes the new-side content, so a different new content
        // produces a different key — the new hunk must be open and require
        // fresh user approval.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nc\n");                             // hunk: (0,1) → ["A"]
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));
        Assert.Equal("accepted", _tracker.Snapshot(SessionId).Proposals[0].Hunks[0].State);

        // Model edits the same line again with DIFFERENT content. Same
        // baseline coords (0,1) but new content "X" instead of "A".
        EditCycle(path, "X\nb\nc\n");

        var newHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.Equal(0, newHunk.BaselineStart);
        Assert.Equal(1, newHunk.BaselineCount);
        Assert.Equal("open", newHunk.State);    // ← was "accepted" before the fix
    }

    [Fact]
    public void Re_edit_at_same_baseline_coords_with_same_content_keeps_accepted()
    {
        // Counterpart to the test above: if the model regenerates the
        // same hunk on a subsequent turn (same coords AND same new
        // content) the prior accept-marker stays applied — the user
        // already approved this exact change.
        var path = MakeFile("foo.cs", "a\nb\nc\n");
        EditCycle(path, "A\nb\nc\n");
        var firstHunk = _tracker.Snapshot(SessionId).Proposals[0].Hunks[0];
        Assert.True(_tracker.AcceptHunk(SessionId, path, firstHunk.BaselineStart, firstHunk.BaselineCount));

        // Same content as before — the model "rewrote" the file but to
        // an identical end state. Could happen on a no-op turn.
        EditCycle(path, "A\nb\nc\n");

        var snap = _tracker.Snapshot(SessionId);
        var stillThere = snap.Proposals[0].Hunks[0];
        Assert.Equal("accepted", stillThere.State);
    }
}
