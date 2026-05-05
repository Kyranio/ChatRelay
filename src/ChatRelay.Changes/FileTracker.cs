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

    /// <summary>True iff Accepted differs from LastApplied — i.e. there are open hunks.</summary>
    public bool HasOpenChanges => !string.Equals(Accepted, LastApplied, StringComparison.Ordinal);

    /// <summary>True iff the current proposal is meaningful (something has changed since baseline).</summary>
    public bool HasProposal => !string.Equals(Baseline, LastApplied, StringComparison.Ordinal);
}

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
