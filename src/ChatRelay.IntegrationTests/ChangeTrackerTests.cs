using System.Text.Json;
using ChatRelay.Changes;
using ChatRelay.Host;

namespace ChatRelay.IntegrationTests;

/// <summary>
/// Direct, in-process tests against <see cref="ChangeTracker"/>. Doesn't
/// spawn a host process — instantiates the tracker, simulates tool_use /
/// tool_result observations and external file edits, asserts state
/// transitions and snapshot output.
///
/// <para>
/// Each test gets its own unique temp directory as the workspace root so
/// the per-test <c>FileSystemWatcher</c> instances can't interfere. The
/// watcher does fire real OS events for our test-driven file writes, but
/// its callback is idempotent (it just re-checks state under the same
/// session lock our explicit calls take) so background events arriving
/// out of order remain harmless.
/// </para>
/// </summary>
public sealed class ChangeTrackerTests : IDisposable
{
    readonly string _workspace;
    readonly ChangeTracker _tracker;
    const string SessionId = "test-session";

    public ChangeTrackerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ChatRelayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _tracker = new ChangeTracker { WorkspaceRoot = _workspace };
    }

    public void Dispose()
    {
        // Tear the watcher down before deleting the directory; otherwise the
        // FileSystemWatcher complains in the background and the cleanup races.
        _tracker.WorkspaceRoot = null;
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // ---- Test helpers ------------------------------------------------

    string MakeFile(string name, string content)
    {
        var path = Path.Combine(_workspace, name);
        WriteWithRetry(path, content);
        return path;
    }

    /// <summary>
    /// File.WriteAllText that retries on sharing violations. The real
    /// FileSystemWatcher fires asynchronously for our test writes and its
    /// callback opens the file for reading; with default sharing those
    /// reads can briefly conflict with subsequent test writes. The
    /// watcher's read in production now uses FileShare.ReadWrite, but
    /// other tools (AV scanners, file-system indexers) can still race.
    /// </summary>
    static void WriteWithRetry(string path, string content)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.WriteAllText(path, content); return; }
            catch (IOException) when (attempt < 9) { Thread.Sleep(20); }
        }
        // Last attempt — let any exception escape so the test fails loud.
        File.WriteAllText(path, content);
    }

    static ToolCallObservation Edit(string filePath, ToolCallPhase phase) => new()
    {
        ToolName = "Edit",
        InputJson = JsonSerializer.Serialize(new { file_path = filePath }),
        Phase = phase,
    };

    /// <summary>
    /// One full Edit cycle: pre-write Observe, swap disk content, post-write
    /// Observe. Simulates what the adapter does when Claude runs an Edit /
    /// Write tool against the file.
    /// </summary>
    void EditCycle(string path, string newContent)
    {
        _tracker.Observe(SessionId, Edit(path, ToolCallPhase.Requested));
        WriteWithRetry(path, newContent);
        _tracker.Observe(SessionId, Edit(path, ToolCallPhase.Completed));
    }

    // ---- Core state machine ------------------------------------------

    [Fact]
    public void Edit_cycle_creates_open_proposal_with_correct_diff()
    {
        var path = MakeFile("foo.cs", "line 1\nline 2\n");
        EditCycle(path, "line 1\nline 2\nline 3\n");

        var snap = _tracker.Snapshot(SessionId);
        var p = Assert.Single(snap.Proposals);
        Assert.Equal("open", p.State);
        Assert.Equal(1, p.LinesAdded);
        Assert.Equal(0, p.LinesRemoved);
        Assert.Empty(snap.Denials);
    }

    [Fact]
    public void Accept_marks_proposal_accepted_without_touching_disk()
    {
        var path = MakeFile("foo.cs", "before");
        EditCycle(path, "after");

        Assert.True(_tracker.Accept(SessionId, path));

        Assert.Equal("after", File.ReadAllText(path));     // disk untouched
        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        Assert.Equal("accepted", p.State);
    }

    // ---- Deny-after-accept regression --------------------------------

    [Fact]
    public void Deny_after_accept_reverts_disk_to_baseline_and_creates_denial()
    {
        // Regression: Deny used to short-circuit when Accepted == LastApplied
        // (which is exactly the post-accept state), so undo-after-accept
        // looked like a no-op. Fix reverts to Baseline regardless of state.
        var path = MakeFile("foo.cs", "before");
        EditCycle(path, "after");
        _tracker.Accept(SessionId, path);
        Assert.Equal("after", File.ReadAllText(path));

        Assert.True(_tracker.Deny(SessionId, path));

        Assert.Equal("before", File.ReadAllText(path));
        var snap = _tracker.Snapshot(SessionId);
        Assert.Empty(snap.Proposals);
        var d = Assert.Single(snap.Denials);
        Assert.Single(d.Entries);
        Assert.True(d.Entries[0].CanRedo);
    }

    [Fact]
    public void Redo_writes_model_content_back_and_clears_denial()
    {
        var path = MakeFile("foo.cs", "before");
        EditCycle(path, "after");
        _tracker.Deny(SessionId, path);

        var denialId = _tracker.Snapshot(SessionId).Denials[0].Entries[0].Id;
        Assert.True(_tracker.RedoDenial(SessionId, path, denialId));

        Assert.Equal("after", File.ReadAllText(path));
        var snap = _tracker.Snapshot(SessionId);
        Assert.Empty(snap.Denials);
        var p = Assert.Single(snap.Proposals);
        // Redo lifts the file back to a proposal but does NOT auto-accept;
        // the user has to decide again.
        Assert.Equal("open", p.State);
    }

    // ---- External-edit invalidation ---------------------------------

    [Fact]
    public void External_edit_drops_denials_whose_post_deny_state_no_longer_matches()
    {
        var path = MakeFile("foo.cs", "before");
        EditCycle(path, "after");
        _tracker.Deny(SessionId, path);     // disk back to "before", denial recorded

        // User manually edits to something different.
        WriteWithRetry(path, "user-edit");
        _tracker.OnExternalFileChange(path);

        var snap = _tracker.Snapshot(SessionId);
        Assert.Empty(snap.Denials);
    }

    // ---- Phase 3.1 fixes --------------------------------------------

    [Fact]
    public void External_edit_drops_tracker_entry_when_state_clean()
    {
        // After a deny + an external edit that clears the lone denial, the
        // file's tracker entry has !HasProposal AND no denials. The fix
        // drops the entry so the next Claude touch captures a fresh
        // Baseline reflecting the user's intermediate work — without this,
        // the next +N/−M would diff against an obsolete pre-user-edit
        // Baseline and the user's removed lines would be invisible.
        var path = MakeFile("foo.cs", "before");
        EditCycle(path, "after");
        _tracker.Deny(SessionId, path);

        WriteWithRetry(path, "user-edit");
        _tracker.OnExternalFileChange(path);

        // Now Claude touches it again, replacing the user's content.
        EditCycle(path, "model-replacement");

        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        // Baseline should be "user-edit" (captured at the new tool_use),
        // not "before". Diff therefore counts user's line as removed.
        Assert.Equal(1, p.LinesAdded);
        Assert.Equal(1, p.LinesRemoved);
    }

    [Fact]
    public void External_edit_skipped_while_ExpectingWrite_is_set()
    {
        // Watcher events that fire between tool_use and tool_result must
        // not be treated as external — that's the model's own pending
        // write echoing back through the FS, not a user edit.
        var path = MakeFile("foo.cs", "baseline");
        EditCycle(path, "model-write");
        _tracker.Deny(SessionId, path);
        // After deny: Baseline = LastApplied = "baseline", one denial.
        var initialDenials = _tracker.Snapshot(SessionId).Denials[0].Entries.Count;
        Assert.Equal(1, initialDenials);

        // Simulate the next turn: Requested fires, then mid-write the
        // watcher catches the partially-written file. ExpectingWrite=true
        // should make OnExternalFileChange skip without dropping the
        // denial (which would otherwise look stale because mid-write disk
        // doesn't match DiskContentAtDeny).
        _tracker.Observe(SessionId, Edit(path, ToolCallPhase.Requested));
        WriteWithRetry(path, "in-flight model write");
        _tracker.OnExternalFileChange(path);

        // Denial still there because we honored ExpectingWrite.
        Assert.Single(_tracker.Snapshot(SessionId).Denials[0].Entries);
    }

    [Fact]
    public void Tool_use_refreshes_baseline_when_disk_drifted_between_turns()
    {
        // Race-window backstop: user edits, then asks Claude faster than
        // the FS event ferries through the watcher path. EnsureBaseline
        // must recheck disk and refresh state so this turn's diff captures
        // the user's intermediate work as the starting point.
        var path = MakeFile("foo.cs", "v1");
        EditCycle(path, "v2-claude");
        _tracker.Deny(SessionId, path);
        // Disk is back at "v1".

        // User edits — and immediately (before the watcher event lands)
        // Claude is asked to edit again. We simulate that by NOT calling
        // OnExternalFileChange before the next Observe.
        WriteWithRetry(path, "v3-user");

        EditCycle(path, "v4-claude");

        var p = Assert.Single(_tracker.Snapshot(SessionId).Proposals);
        // Baseline must have been refreshed to "v3-user" inside the
        // tool_use path, otherwise the diff would still be against "v1"
        // and miss the user's change.
        Assert.Equal(1, p.LinesAdded);
        Assert.Equal(1, p.LinesRemoved);
        // Stale denial referencing the previous baseline state must be
        // dropped — not redoable any more anyway.
        Assert.Empty(_tracker.Snapshot(SessionId).Denials);
    }

    // ---- Workspace scoping ------------------------------------------

    [Fact]
    public void Tool_use_outside_workspace_is_ignored()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ChatRelayOutside_" + Guid.NewGuid().ToString("N") + ".cs");
        WriteWithRetry(outside, "irrelevant");
        try
        {
            EditCycle(outside, "still irrelevant");
            Assert.Empty(_tracker.Snapshot(SessionId).Proposals);
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void Unknown_tool_names_are_silently_ignored()
    {
        var path = MakeFile("foo.cs", "content");
        _tracker.Observe(SessionId, new ToolCallObservation
        {
            ToolName = "Read",
            InputJson = JsonSerializer.Serialize(new { file_path = path }),
            Phase = ToolCallPhase.Requested,
        });
        Assert.Empty(_tracker.Snapshot(SessionId).Proposals);
    }
}
