# ChatRelay Protocol v0

Wire contract between a ChatRelay shell (IDE extension or standalone app) and
the ChatRelay host process.

**Status:** `v0` — pre-stable. Breaking changes possible until `v1.0`. Pin the
exact version string on both sides.

---

## 1. Transport

**JSON-RPC 2.0** over the host's **stdin/stdout**, with LSP-style framing:

```
Content-Length: <n>\r\n
\r\n
<n bytes of UTF-8 JSON>
```

One request or notification per message. Reference implementation on the host
uses `StreamJsonRpc`. Shells implement a client in whatever their IDE provides.

Only local stdio in v0. Named pipes / TCP / WebSocket are not in scope.

---

## 2. Lifecycle

```
shell spawns host exe
shell → initialize
host  → initialize result
… many requests + notifications …
shell → shutdown
host  → shutdown result
shell closes pipes; host exits
```

The shell owns the host's lifetime. If the shell is killed, the host exits when
its stdin closes.

---

## 3. Conventions

- Every request takes a **single object as its `params`** (named, not
  positional).
- Every response with data returns a **single result object**.
- IDs are **strings**. Timestamps are **ISO 8601** in UTC.
- `null` fields MAY be omitted.
- Method names use `camelCase`. Notifications are prefixed with `on`.

### Paths

The protocol is IDE-neutral. `.sln` / `.csproj` / `.xcodeproj` / `package.json`
are not special. Everything is a **plain filesystem path**.

- Workspace paths are **directories**. If a shell has a file it considers the
  project root (e.g. a `.sln` file), it MUST resolve to the containing
  directory before sending.
- Paths are sent as the OS-native form the shell sees (backslashes on Windows
  are fine). The host normalises internally — absolute, trimmed, case-folded
  on Windows — for indexing and comparison. Display always uses the shell's
  original string.
- Reference paths (in `sendPrompt`) are absolute OR relative to the current
  workspace.

## 4. Workspace

A **workspace** is a single filesystem path the shell is currently "working
in." It serves three purposes host-side, in unison:

1. **Session scope** — sessions are grouped by workspace path. `listSessions`
   returns only sessions that belong to the current workspace.
2. **Adapter working directory** — adapters that care about a CWD (Claude
   CLI, future shell-execution tooling) use this path.
3. **Project-scoped config** — `.chatrelay.mcp.json` at the workspace root is
   merged with the global config.

A workspace path can be `null`. In that case sessions group under a shared
"no workspace" bucket and project-scoped config is skipped.

The workspace is **connection-scoped**, not per-request. Shell sends it at
`initialize`, updates it with `setWorkspace` if the user opens a different
project. Every other request implicitly uses the current workspace.

---

## 5. Requests

### `initialize`

First message. Must be called before any other request.

```json
{ "clientName": "ChatRelay.VisualStudio", "clientVersion": "0.1.0",
  "protocolVersion": "0",
  "workspacePath": "C:\\src\\MyApp" }
```

`workspacePath` may be `null`. See §4.

Result:

```json
{ "serverName": "ChatRelay.Host", "serverVersion": "0.1.0",
  "protocolVersion": "0" }
```

If `protocolVersion` doesn't match, the host fails the request with
`ProtocolMismatch` (see §9).

### `setWorkspace`

Change the current workspace. Sessions listed, MCP project config, adapter
CWD all re-scope to the new path. In-flight turns continue uninterrupted but
belong to the old workspace's session until they complete.

```json
{ "path": "C:\\src\\OtherApp" }
```

`path` may be `null`. No result.

### `shutdown`

Flush sessions, stop MCP servers, release broker pipes. No params, no result.
The host exits after responding.

### `listAdapters`

No params.

```json
[
  { "id": "claude-cli", "name": "Claude CLI", "available": true },
  { "id": "claude-api", "name": "Claude API", "available": false },
  { "id": "ollama",     "name": "Ollama",     "available": true }
]
```

### `refreshAdapters`

Re-probe availability. No params, no result. Emits `onAdaptersChanged` when
done.

### `listModels`

No params.

```json
[
  { "id": "claude-opus-4-5-20250929", "adapterId": "claude-api",
    "displayName": "Claude Opus 4.5" },
  { "id": "llama3.2:3b", "adapterId": "ollama",
    "displayName": "llama3.2:3b" }
]
```

### `listSessions`

No params. Returns sessions for the current workspace, sorted newest-first
by `lastMessageAt`. Sessions with zero user/assistant turns are filtered
out so empty placeholders never surface to the shell.

```json
[
  { "id": "0", "label": "Refactor auth",
    "adapterId": "claude-api", "modelId": "claude-opus-4-5-20250929",
    "lastMessageAt": "2026-04-24T11:22:00Z" }
]
```

`lastMessageAt` is optional (omitted on legacy-format buckets without
per-message timestamps).

### `getActiveSession`

No params. Returns the id of the session most recently opened in this
workspace, or null. The host writes this on every successful
`openSession`. The shell uses it to restore the user's last position
on launch.

### `openSession`

Params:
```json
{ "sessionId": "a1b2c3d4" }
```

`sessionId` is optional. If null, the host creates a new empty session in the
current workspace and returns its id. Result:

```json
{
  "sessionId": "a1b2c3d4",
  "adapterId": "claude-api",
  "modelId": "claude-opus-4-5-20250929",
  "messages": [
    { "role": "user",      "text": "…", "timestamp": "2026-04-24T11:00:00Z" },
    { "role": "assistant", "text": "…", "timestamp": "2026-04-24T11:00:05Z",
      "usage": { "inputTokens": 512, "outputTokens": 128, "costUsd": 0.004 } }
  ]
}
```

### `deleteSession`

Params: `{ "sessionId": "…" }`. No result.

### `sendPrompt`

Start a turn. Returns as soon as the host has accepted the request; completion
is signaled via `onTurnDone`.

```json
{
  "sessionId": "a1b2c3d4",
  "adapterId": "claude-api",
  "modelId": "claude-opus-4-5-20250929",
  "text": "Explain this selection.",
  "references": [
    { "path": "src/Auth.cs", "fullContent": "…",
      "ranges": [ { "start": 10, "end": 42 } ] }
  ]
}
```

`references[].fullContent` is set for whole-file refs; `ranges` is set for
selection refs. Both may be present when the selection covers the whole file
and the shell chose to send both; the host treats `fullContent` as canonical.

If the session has never been sent to, `adapterId` / `modelId` persist on the
session. On later turns they can be omitted to reuse the stored values, or
provided to switch.

Result: `{ "accepted": true }`. Events follow.

### `cancelTurn`

```json
{ "sessionId": "a1b2c3d4" }
```

Cancels the in-flight turn, if any. Events already in flight may still be
delivered; no new events arrive after cancellation. Always produces an
`onTurnDone` with `cancelled: true`.

### `respondPermission`

Reply to a pending `onPermissionRequest`.

```json
{ "requestId": "p-9f3a", "decision": "allow", "remember": true }
```

- `decision`: `"allow"` or `"deny"`.
- `remember`: if `true`, the host caches the decision by tool + argument
  shape. Subsequent matching requests are auto-answered without bothering the
  shell.

No result.

### MCP

#### `listMcpServers`

```json
[
  { "id": "filesystem", "name": "filesystem",
    "status": "running", "source": "global",
    "tools": [ { "name": "read_file", "description": "…" } ] },
  { "id": "sqlite", "name": "sqlite",
    "status": "error", "source": "project",
    "errorMessage": "executable not found on PATH" }
]
```

`status`: `"stopped" | "starting" | "running" | "error"`.
`source`: `"global" | "project"`.

#### `startMcpServer` / `stopMcpServer` / `restartMcpServer`

Params: `{ "id": "…" }`. No result. Status changes arrive via
`onMcpServerChanged`.

#### `setMcpServerEnabled`

```json
{ "id": "filesystem", "enabled": false }
```

Persists to settings. Affects what the host advertises to models.

#### `setMcpToolEnabled`

```json
{ "serverId": "filesystem", "toolName": "write_file", "enabled": false }
```

#### `listMcpFiles`

```json
[
  { "path": "C:\\Users\\me\\AppData\\Local\\ChatRelay\\.chatrelay.mcp.json",
    "scope": "global" },
  { "path": "C:\\src\\MyApp\\.chatrelay.mcp.json",
    "scope": "project" }
]
```

#### `addMcpFile` / `removeMcpFile`

Add: `{ "path": "…", "scope": "global" | "project" }`. Remove: `{ "path": "…" }`.

### Settings

#### `getSettings`

No params. Returns the current settings blob. Exact shape lives in §8.

#### `updateSettings`

Params: `{ "patch": <partial Settings object> }`. The host merges the patch
into the stored settings, persists, and returns the full updated blob.

---

## 6. Notifications (host → shell)

### `onAdaptersChanged`

Sent after `refreshAdapters` completes or on any availability change. No
payload — shell is expected to re-query.

### `onModelsChanged`

Same pattern. No payload.

### `onAssistantChunk`

```json
{ "sessionId": "a1b2c3d4", "text": "partial markdown…" }
```

Multiple chunks concatenate into the current assistant message.

### `onThinkingChunk`

Same shape. Signals streamed reasoning content (Claude thinking, etc).

### `onModelInfo`

```json
{ "sessionId": "a1b2c3d4", "modelDisplayName": "Claude Opus 4.5" }
```

Sent once per turn if the adapter reports a canonical model name different
from what the shell sent.

### `onUsage`

```json
{ "sessionId": "a1b2c3d4",
  "inputTokens": 1024, "outputTokens": 256,
  "cacheCreateTokens": 0, "cacheReadTokens": 0,
  "costUsd": 0.0042 }
```

May fire multiple times in a multi-turn tool-use loop; the shell displays the
latest values or the sum as preferred. Fields may be 0 or omitted when the
adapter doesn't report them.

### `onSessionIdAssigned`

```json
{ "sessionId": "a1b2c3d4", "assignedId": "cli-sess-f00ba4" }
```

Some adapters (Claude CLI) assign their own session id on the first turn. The
host persists both ids; the shell doesn't need to act on this unless it wants
to display the remote id.

### `onPermissionRequest`

```json
{
  "requestId": "p-9f3a",
  "sessionId": "a1b2c3d4",
  "toolName": "Bash",
  "input":    { "command": "rm -rf /tmp/foo" }
}
```

Shell must eventually call `respondPermission` with the matching `requestId`.
If the shell never responds, the adapter will time out on its own.

Shells MAY render tool-specific UI based on `toolName` — for example,
computing a file diff for a `write_file`-style tool and showing it inline
with per-hunk context. The host stays neutral about presentation; `input`
carries whatever the model passed.

### `onMcpServerChanged`

Fired whenever a server transitions status or tool list changes. Full snapshot
payload (same shape as a `listMcpServers` entry) so shells don't need to diff:

```json
{ "id": "filesystem", "name": "filesystem",
  "status": "running", "source": "global",
  "tools": [ { "name": "read_file", "description": "…" } ] }
```

### `onError`

```json
{ "sessionId": "a1b2c3d4", "message": "HTTP 401 from Anthropic API" }
```

Non-fatal error the shell should surface to the user. Fatal errors come back
as request failures (§9).

### `onTurnDone`

```json
{ "sessionId": "a1b2c3d4", "cancelled": false }
```

Terminal event for a turn. Always fires exactly once per `sendPrompt`.

---

## 7. Notifications (shell → host)

None in v0. Shell communicates only via requests.

---

## 8. Shapes

### `Session` (on-disk + returned from `openSession`)

```
id              string
label           string          auto-derived from the first user prompt
adapterId       string
modelId         string
createdAt       timestamp
updatedAt       timestamp
messages        Message[]
```

### `Message`

```
role            "user" | "assistant"
text            string
timestamp       timestamp       optional (omitted on legacy-format bubbles)
thinking        string          optional (assistant messages only)
references      Reference[]     optional (user messages only)
usage           Usage           optional (assistant messages only)
model           string          optional (assistant messages only) — concrete model the turn used
```

### `Reference`

```
path            string          absolute, or relative to the workspace
fullContent     string          optional, set for whole-file refs
ranges          LineRange[]     optional, set for selection refs
```

### `LineRange`

```
start           int             1-based, inclusive
end             int             1-based, inclusive
```

### `Usage`

Same fields as `onUsage` payload.

### `Settings`

v0 surface (additive; unknown fields preserved on round-trip):

```
general:
  autoAttachActiveFile       bool
  thinkingExpandedByDefault  bool
  additionalDirectories      string[]
permissions:
  disallowedTools            string[]
  disabledMcpTools           string[]     "mcp__server__tool" ids
  disabledMcpServers         string[]
mcpFiles:
  - path: string
    scope: "global" | "project"
```

---

## 9. Errors

JSON-RPC error codes follow the standard `-32700..-32603` range for parse /
invocation errors. Application-level failures use this range:

```
-32001  ProtocolMismatch       initialize version mismatch
-32002  SessionNotFound        unknown sessionId
-32003  AdapterUnavailable     adapter failed probe
-32004  ModelNotFound          modelId unknown to the selected adapter
-32005  McpServerNotFound      id unknown
-32006  SettingsInvalid        patch failed validation
-32099  Internal               anything else
```

Error `data` field, when present, is an object with a `details` string.

---

## 10. Cancellation

JSON-RPC doesn't define cancellation natively; this protocol uses LSP's
convention:

```json
{ "jsonrpc": "2.0", "method": "$/cancelRequest", "params": { "id": <reqId> } }
```

Applies to `sendPrompt` and any long-running operation. The host should return
the cancelled request with a `RequestCancelled` error (code `-32800`).

For `sendPrompt` specifically, `cancelTurn` is the domain-level equivalent and
is preferred — it gives the host a chance to flush a final `onTurnDone`.

---

## 11. Example: a single turn

```
→ { "jsonrpc":"2.0","id":1,"method":"sendPrompt",
     "params":{"sessionId":"s1","adapterId":"claude-api",
               "modelId":"claude-opus-4-5-20250929",
               "text":"What does this do?",
               "references":[{"path":"src/Foo.cs",
                              "ranges":[{"start":10,"end":20}],
                              "fullContent":null}]}}

← { "jsonrpc":"2.0","id":1,"result":{"accepted":true} }

← { "jsonrpc":"2.0","method":"onAssistantChunk",
     "params":{"sessionId":"s1","text":"This function "}}
← { "jsonrpc":"2.0","method":"onAssistantChunk",
     "params":{"sessionId":"s1","text":"normalises the path…"}}
← { "jsonrpc":"2.0","method":"onUsage",
     "params":{"sessionId":"s1","inputTokens":312,"outputTokens":44,
               "costUsd":0.0012}}
← { "jsonrpc":"2.0","method":"onTurnDone",
     "params":{"sessionId":"s1","cancelled":false}}
```

---

## 12. Out of scope for v0

Listed explicitly so nobody is surprised:

- Multi-user / multi-tenant
- Remote host over network (only local stdio)
- Authentication
- Capability negotiation in `initialize` (added in v1 if needed)
- Structured tool-use visualisation events (today's UI folds tool calls into
  the assistant's final message; a v1 extension will expose them as their own
  events)
- Theme / color / font config (shell-only concerns)
- HTTP / SSE MCP transports (stdio MCP servers only, as in v0 of the
  extension)
- **Partial acceptance of a tool call** — approving a proposed edit but
  modifying its arguments first. When built, this is one optional
  `modifiedInput` field on `respondPermission`. v0 shells get full-or-nothing
  accept / deny.
- **Atomic review of a multi-step change set** — the model proposes several
  file edits in one turn and the user reviews them together. When built,
  this is one new notification (`onProposedChangeset`) that groups related
  permission requests, plus a matching batch-response request. In v0, each
  tool call fires its own `onPermissionRequest` independently.
