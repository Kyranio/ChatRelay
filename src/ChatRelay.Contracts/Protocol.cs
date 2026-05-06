using System;
using System.Collections.Generic;
using ChatRelay.Settings;

namespace ChatRelay.Host;

// Wire records shared by the host (server) and any .NET shell (client). The
// shapes here are the contract — adding / removing / renaming a field here
// breaks every shell. Bump PROTOCOL.md and the protocolVersion handshake
// when you change anything.
//
// Camel-case naming on the wire is enforced by the StreamJsonRpc
// SystemTextJsonFormatter both sides configure; case-insensitive reads
// preserve compatibility with older settings.json files saved in PascalCase.

// Lifecycle + workspace ------------------------------------------------------

public record InitializeParams(string ClientName, string ClientVersion, string ProtocolVersion, string? WorkspacePath);
public record InitializeResult(string ServerName, string ServerVersion, string ProtocolVersion);
public record SetWorkspaceParams(string? Path);

// Adapters + models ----------------------------------------------------------

public record AdapterInfo(string Id, string Name, bool Available);
public record ModelSummary(string Id, string AdapterId, string DisplayName);

// References -----------------------------------------------------------------

public record ReferenceRange(int Start, int End);
public record Reference(string Path, string? FullContent, IReadOnlyList<ReferenceRange>? Ranges);

// Turn -----------------------------------------------------------------------

public record SendPromptParams(string SessionId, string? AdapterId, string? ModelId, string Text, IReadOnlyList<Reference>? References);
public record SendPromptResult(bool Accepted);
public record CancelTurnParams(string SessionId);

public record AssistantChunkParams(string SessionId, string Text);
public record ThinkingChunkParams(string SessionId, string Text);
public record ModelInfoEvent(string SessionId, string ModelDisplayName);
public record SessionIdAssignedParams(string SessionId, string AssignedId);
public record UsageParams(string SessionId, int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreateTokens, double? CostUsd);
public record ErrorEvent(string SessionId, string Message);
public record TurnDoneParams(string SessionId, bool Cancelled);

// Sessions -------------------------------------------------------------------

public record SessionSummary(string Id, string Label, string? AdapterId, string? ModelId, DateTime? LastMessageAt = null);
public record OpenSessionParams(string? SessionId);
public record OpenSessionResult(string SessionId, string? AdapterId, string? ModelId, string? DraftText, IReadOnlyList<SessionMessage> Messages);
public record DeleteSessionParams(string SessionId);
public record SetSessionDraftParams(string SessionId, string Text);

public record SessionMessage(string Role, string Text, string? Thinking, UsagePayload? Usage, string? Model = null, DateTime? Timestamp = null);
public record UsagePayload(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreateTokens, double? CostUsd);

// Settings -------------------------------------------------------------------
//
// UpdateSettingsParams takes the same `ExtensionSettings` shape that
// SettingsStore persists to disk — they are wire and on-disk DTO at once.
// The shell sends a partial / full ExtensionSettings, the host merges and
// re-saves.

public record UpdateSettingsParams(ExtensionSettings Patch);

// MCP ------------------------------------------------------------------------

public record McpToolSummary(string Name, string? Description);
public record McpServerSummary(
    string Id, string Name, string Status, string? ErrorMessage,
    string Source, IReadOnlyList<McpToolSummary> Tools);
public record McpServerIdParams(string Id);
public record SetMcpServerEnabledParams(string Id, bool Enabled);
public record SetMcpToolEnabledParams(string ServerId, string ToolName, bool Enabled);
public record McpFileSummary(string Path, string Scope);
public record AddMcpFileParams(string Path, string Scope);
public record RemoveMcpFileParams(string Path);

// Permissions ----------------------------------------------------------------

public record PermissionRequestEvent(string RequestId, string SessionId, string ToolName, string InputJson);
public record RespondPermissionParams(string RequestId, string Decision, bool Remember);

// Change tracking ------------------------------------------------------------
//
// Volatile, in-memory only on the host. Per-session. Cleared when the host
// process dies (i.e. when VS closes). Tracks files the model has written
// during this VS session so the user can review and accept / deny each
// proposal independently. Edits are always already on disk — the tracker
// just records what changed and how to undo.
//
// "open" → user hasn't decided. Shown in the main changes list.
// "accepted" → user kept the change. Stays in main list, accept button hidden.
// Denied entries live on a parallel list per file (the collapsible section).
//
// One row per file, aggregating every edit the model made to it during the
// session — the line counts are baseline-vs-current diff, not per-edit deltas.

public record SessionChangesSnapshot(
    string SessionId,
    IReadOnlyList<ChangeProposal> Proposals,
    IReadOnlyList<DenialGroup> Denials);

public record ChangeProposal(
    string FilePath,
    string AbsolutePath,
    int LinesAdded,
    int LinesRemoved,
    string State,                            // "open" | "accepted" — file-level rollup
    IReadOnlyList<HunkInfo> Hunks);          // empty when nothing left to show

/// <summary>
/// One model-vs-baseline edit hunk in a tracked file. Coordinates are
/// 0-based line indices. Use <c>BaselineStart</c>/<c>BaselineCount</c> as
/// the stable identifier for accept / reject RPCs — host re-computes the
/// diff and locates the matching hunk by those coordinates so the click
/// stays correct even if the snapshot has been refreshed in between.
/// </summary>
public record HunkInfo(
    int BaselineStart,
    int BaselineCount,
    int CurrentStart,
    int CurrentCount,
    IReadOnlyList<string> BaselineLines,
    IReadOnlyList<string> CurrentLines,
    string State,                // "open" | "accepted"
    string? Model);              // display name of the model that authored this file's edits

public record DenialGroup(
    string FilePath,
    string AbsolutePath,
    IReadOnlyList<DeniedChangeSummary> Entries);

public record DeniedChangeSummary(
    string Id,
    int LinesAdded,
    int LinesRemoved,
    DateTime DeniedAt,
    bool CanRedo);              // false once the file has been modified externally

public record ListChangesParams(string SessionId);
public record AcceptChangeParams(string SessionId, string FilePath);
public record DenyChangeParams(string SessionId, string FilePath);
public record RedoDeniedChangeParams(string SessionId, string FilePath, string DenialId);
public record BulkChangesParams(string SessionId);

// Per-hunk operations. BaselineStart + BaselineCount identify the hunk in
// stable Baseline coordinates; the host re-runs the diff and finds the
// matching hunk on each call. AcceptHunk only succeeds on hunks that are
// currently in the "open" state; RejectHunk works on either state and
// always lands the file's region back at Baseline content.
public record AcceptHunkParams(string SessionId, string FilePath, int BaselineStart, int BaselineCount);
public record RejectHunkParams(string SessionId, string FilePath, int BaselineStart, int BaselineCount);

// Editor-side invalidation: the user has typed inside an accepted hunk's
// current line range, so its accept-marker should drop in the snapshot.
// Sent before the user has saved — the host can't infer this from the
// watcher because the buffer change hasn't reached disk yet. Coordinates
// match the same shape as Accept/Reject so the host can locate the hunk
// against the current diff.
public record InvalidateAcceptedHunkParams(string SessionId, string FilePath, int BaselineStart, int BaselineCount);

public record ChangesUpdatedEvent(SessionChangesSnapshot Snapshot);
