namespace ChatRelay.Changes;

/// <summary>
/// Per-file change accounting in Git-blob style.
///
/// <para>
/// Three blobs:
/// <list type="bullet">
///   <item><c>Baseline</c> — content at first observation in this session.</item>
///   <item><c>Accepted</c> — baseline plus every hunk the user has accepted so
///   far. Phase 1 only does whole-file accept, so <c>Accepted</c> is either
///   equal to <c>Baseline</c> (nothing accepted) or equal to <c>LastApplied</c>
///   (everything accepted).</item>
///   <item><c>LastApplied</c> — current disk content as of the last observed
///   model write.</item>
///  </list>
/// </para>
///
/// <para>
/// Open hunks are <c>diff(Accepted, LastApplied)</c>. The future inline-editor
/// phase will compute per-hunk operations against these blobs without changing
/// the data shape.
/// </para>
///
/// <para>
/// Denied entries store the would-have-been content (so we can redo) plus the
/// post-undo disk content (so we can detect external modification and
/// invalidate the redo button).
/// </para>
/// </summary>
public sealed class FileTracker
{
    public required string AbsolutePath { get; init; }
    public required string DisplayPath { get; init; }    // workspace-relative when possible

    /// <summary>Content at first observation in this session. Restored on whole-file deny.</summary>
    public string Baseline { get; set; } = string.Empty;

    /// <summary>Baseline plus accepted hunks. Phase 1: equals Baseline or LastApplied.</summary>
    public string Accepted { get; set; } = string.Empty;

    /// <summary>Current disk content as last seen post-write.</summary>
    public string LastApplied { get; set; } = string.Empty;

    /// <summary>True once the user has explicitly accepted this file's current diff.</summary>
    public bool IsAccepted { get; set; }

    /// <summary>
    /// Past denials for this file, in deny-order. Each carries enough state
    /// to redo (re-apply) the change if the file hasn't been touched since.
    /// </summary>
    public List<DeniedChangeRecord> Denied { get; } = new();

    /// <summary>
    /// Hunks the user has explicitly accepted, identified by their position
    /// in <see cref="Baseline"/>. <see cref="Accepted"/> is derivable from
    /// <see cref="Baseline"/> + this set, but we maintain it as a blob for
    /// snapshot speed and to keep whole-file accept / deny semantics
    /// uniform with per-hunk operations.
    /// <para>
    /// Stale entries (hunks the model has since reshaped past recognition)
    /// are pruned in <c>UpdateLastAppliedLocked</c> after a fresh write.
    /// </para>
    /// </summary>
    public HashSet<HunkKey> AcceptedHunks { get; } = new();

    /// <summary>True iff Accepted differs from LastApplied — i.e. there are open hunks.</summary>
    public bool HasOpenChanges => !string.Equals(Accepted, LastApplied, StringComparison.Ordinal);

    /// <summary>True iff the current proposal is meaningful (something has changed since baseline).</summary>
    public bool HasProposal => !string.Equals(Baseline, LastApplied, StringComparison.Ordinal);

    /// <summary>
    /// Set true between a tool_use observation and its matching tool_result.
    /// During this window the model's write may already have hit disk and
    /// been picked up by <see cref="WorkspaceWatcher"/> before our own
    /// <c>UpdateLastApplied</c> caught up — the tracker would mis-classify
    /// it as an external edit and clobber state. Honoring this flag in the
    /// watcher path skips that misfire.
    /// </summary>
    public bool ExpectingWrite { get; set; }

    /// <summary>
    /// Display name of the model that most recently touched this file
    /// (e.g. "Claude Sonnet 4.5"). Populated from the session's
    /// <c>CurrentModel</c> at <c>tool_use</c> observation time. Null until
    /// a <c>ModelInfo</c> event has been seen for the session.
    /// <para>
    /// Per-file granularity (rather than per-hunk) — accurate enough for
    /// the "Edited by X" tooltip on accepted hunks, and avoids retaining
    /// extra per-hunk state. If the user mid-conversation switches models
    /// and the new model touches the same file, the latest model wins.
    /// </para>
    /// </summary>
    public string? LastModel { get; set; }
}

/// <summary>
/// Identifies a hunk by its (Baseline-line-start, Baseline-line-count) pair —
/// stable across snapshot recomputes as long as the model hasn't reshaped
/// the file enough to make the hunk unrecognisable.
/// </summary>
public readonly record struct HunkKey(int BaselineStart, int BaselineCount);

/// <summary>
/// One deny entry. Holds the content we'd write back on redo plus the disk
/// content right after the deny. The tracker drops the record outright the
/// moment <c>DiskContentAtDeny</c> stops matching the file's actual content
/// (next model edit, user edit caught by the watcher, or a redo race) — so
/// any entry surviving in <see cref="FileTracker.Denied"/> is, by
/// construction, still redoable.
/// </summary>
public sealed class DeniedChangeRecord
{
    public required string Id { get; init; }                // stable id for the wire
    public required DateTime DeniedAt { get; init; }
    public required string ContentToReapply { get; init; }  // = LastApplied at deny time
    public required string DiskContentAtDeny { get; init; } // = Baseline at deny time (what we wrote)
    public required int LinesAdded { get; init; }           // diff(DiskContentAtDeny, ContentToReapply)
    public required int LinesRemoved { get; init; }
}
