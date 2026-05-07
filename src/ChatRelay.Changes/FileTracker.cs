namespace ChatRelay.Changes;

/// <summary>Per-file change accounting. Baseline = last accepted truth; LastApplied = current disk content.</summary>
public sealed class FileTracker
{
    public required string AbsolutePath { get; init; }
    public required string DisplayPath { get; init; }

    /// <summary>The last accepted truth. Advances on Accept / AcceptHunk so future diffs and Deny revert to here.</summary>
    public string Baseline { get; set; } = string.Empty;

    /// <summary>Current disk content as last seen post-write.</summary>
    public string LastApplied { get; set; } = string.Empty;

    /// <summary>Past denials in deny-order, each redoable while its post-deny disk state hasn't drifted.</summary>
    public List<DeniedChangeRecord> Denied { get; } = new();

    public bool HasProposal => !string.Equals(Baseline, LastApplied, StringComparison.Ordinal);

    /// <summary>True between tool_use and tool_result so the watcher doesn't mis-classify the model's own write as external.</summary>
    public bool ExpectingWrite { get; set; }

    /// <summary>Display name of the model that most recently touched this file.</summary>
    public string? LastModel { get; set; }
}

/// <summary>One deny entry. Holds the redo content plus the post-deny disk content for drift detection.</summary>
public sealed class DeniedChangeRecord
{
    public required string Id { get; init; }
    public required DateTime DeniedAt { get; init; }
    public required string ContentToReapply { get; init; }
    public required string DiskContentAtDeny { get; init; }
    public required int LinesAdded { get; init; }
    public required int LinesRemoved { get; init; }
}
