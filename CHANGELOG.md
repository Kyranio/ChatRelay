# Changelog

All notable changes to ChatRelay are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] — 2026-05-08

Inline change tracking. Every model edit now lights up directly in the
editor with per-hunk accept / reject buttons, a turquoise highlight on
added lines, and a red strip showing what was removed. The chat-side
changelist persists per-file totals (open + accepted) for the lifetime
of the VS session.

### Added
- **In-editor adornments** for model edits — turquoise highlight on
  open hunks, full-width red strip above for removed lines (with
  baseline line numbers in a sibling margin), and a per-hunk
  accept (✓) / reject (↶) button row.
- **Per-hunk accept / reject** alongside the existing per-file flow.
  Accept folds the hunk into Baseline immediately so future diffs and
  Deny revert to that point — not the pre-accept original.
- **CSS-sticky button positioning** — buttons anchor at the hunk's
  visual top, clamp to the viewport top with a small margin as the
  hunk scrolls past, then ride the hunk's bottom off-screen.
- **Per-file accepted counters** in the changelist (volatile, in-memory)
  shown next to the filename so totals stay visible after every change
  is accepted. Session-level rollup in the header strip.
- **Resizable changelist** — capped at ~5 file rows by default with a
  drag-handle above to grow / shrink.
- **DiffPlex line diff** with content-aware coalescing: matched runs
  of whitespace and structural punctuation merge so a model edit shows
  as one logical hunk; substantive code splits.

### Wire (Contracts)
- `ChangeProposal` adds `AcceptedLinesAdded` / `AcceptedLinesRemoved`
  (defaulted, backward-compatible).
- `SessionChangesSnapshot` adds session-level
  `AcceptedLinesAdded` / `AcceptedLinesRemoved`.

### Fixed
- **Deny-after-accept regression** — `Deny` on a follow-up change used
  to revert the file to the pre-accept original because Baseline
  wasn't advanced on accept. Now accepts fold into Baseline so Deny
  reverts to the last accepted truth.
- **Adornments survive a host write that reloads the buffer.** Tracking
  spans built before VS finishes its `ContentLoadedFromDisk` cycle no
  longer collapse and orphan the highlight; the manager defers when
  the buffer hasn't caught up and rebuilds when the reload completes.
- **Buttons stay focus-stable.** Moved from `ISpaceReservationAgent`
  popups (which lose focus when the editor gets focus) to WPF
  adornments on a layer above Text.
- **Test-run config mismatch** — `HostFixture` now derives the host
  DLL path from the same build config the tests are running under,
  so a Debug rebuild + `dotnet test -c Release` doesn't silently
  load the wrong DLL.

### CI
- Build + integration tests run on every PR into `dev` or `master`.
- CI rebuild passes `DeployExtension=false` so the VS 2022 runner
  can build a VS 2026-targeted VSIX without trying to deploy to a
  non-existent experimental hive.

### Tooling
- DiffPlex 1.9.0 added as a project reference in `ChatRelay.Changes`.

## [0.1.1] — 2026-05-01

First versioned release with shipped artifact. Folds in everything
that had been accumulating under `[Unreleased]` since `0.1.0` was
written (architecture rewire, chat UX, MCP HTTP/SSE transport, etc.)
plus two new bug fixes called out below.

### Fixed
- **Permission-bubble buttons follow the VS theme.** Deny / Allow once
  / Allow always were plain WPF `Button`s with only padding and cursor
  set, falling back to Win32-default gray chrome that ignored Dark /
  Light / Blue. Now use themed brushes (`EnvironmentColors`) with
  red / neutral / brand-turquoise accent borders.
- **Claude CLI sessions resume across turns.** `BuildRequest` was
  reading the session id from a per-turn variable that hadn't been
  populated yet, so every send went out without `--resume` and Claude
  lost context after the first message. Now reads the persisted id
  from `SessionStore` directly. Pre-existing bug, not a regression.

### Architecture
- **Wire DTOs** centralised in `ChatRelay.Contracts` (netstandard2.0) —
  one assembly referenced by both the net48 shell and the net10 host;
  the duplicate `Protocol.cs` mirror and parallel `SettingsDto` family
  are gone.
- **Vertical slices** peeled out of Core: `Logging`, `Settings`,
  `Sessions`, `Permissions`. Each in its own project with a tiny
  dependency surface. Core now holds only the Backends + MCP frameworks
  and small Paths utilities.
- **MCP transport abstraction** (`IMcpTransport` + `McpTransports.CreateFor`).
  Stdio extracted; **HTTP / SSE transport added** for remote MCP
  servers (single configured URL, accepts `"type": "http"` or
  `"type": "sse"`, sticky `Mcp-Session-Id` header, polite `DELETE` on
  dispose). Adding another transport is a single new class + one
  switch case.
- **Chat shell split** into `ChatViewModel` (state + host calls + logic)
  and `ChatControl` (rendering + threading).
- **Phase 3 (DI / modules) skipped** — overkill at this scale. Recorded
  here so the omission is explicit.
- See `ARCHITECTURE.md` for the layer / shell-internals / state diagrams.

### Chat UX
- **Home state.** No "New chat" placeholders — the chat opens to a
  "Send a message to start chatting" hint when no session is active.
  The first message creates the session lazily; it appears in the
  dropdown only after it has content.
- **Recent-chats list** (top 5, clickable) on the home pane — fills
  in once the background session loader finishes.
- **Loading overlay** holds until the host is ready and the home / restored
  session is rendered.
- **Background session load** with bounded workspace wait (15 s) —
  models + home come up first; sessions stream in shortly after.
- **Auto-restore** the last-used session on launch, but only if the user
  hasn't already typed / pinned a reference / clicked something.
- **Last-used = most recent message** (host-side
  `listSessions` sorts by `LastMessageAt`); the host also persists
  `ActiveSessionId` per workspace.
- **Session controls lock during a turn** (picker, New, Delete, Send).
  Esc still cancels.
- **Per-bubble timestamps** (machine locale, today = time only).
- **Per-bubble model name.** Assistant bubbles show the actual model
  used ("Sonnet 4.5", etc.) instead of "Claude" or the picker label;
  live streaming bubbles update from the first `onModelInfo` event.

### Wire (Contracts)
- `SessionMessage` adds optional `Model`, `Timestamp`.
- `SessionSummary` adds optional `LastMessageAt`; `listSessions` sorts
  newest-first and filters out empty sessions.
- `getActiveSession` RPC method.

### Reliability
- **WPF `Dispatcher`** now used directly for UI marshalling
  (`UiThread.SwitchToUi` / `OnUi`). `JoinableTaskFactory.SwitchToMainThreadAsync`
  was inlining synchronously in tool-window paths and silently
  dropping `ObservableCollection` updates.
- **Wait-for-condition timeouts** replaced with proper async primitives
  (`WaitForExitAsync`, linked CTS + `CancelAfter`). No more
  `Task.Delay(N)` guesses.
- **Markdown re-theme walk** snapshots WPF text-element collections
  before iterating — fixes "Collection was modified" crashes on
  responses with markdown lists / nested formatting.
- **HTTP probes / version probes** drive their own deadlines via linked
  CTS instead of static `HttpClient.Timeout`.

### Tooling
- `ChatRelay.IntegrationTests` (xUnit + StreamJsonRpc) — spawns the host
  and asserts the protocol surface. 10 tests, ~4 s.

### Removed
- Permission-mode dropdown (was wire-incomplete; permissions still
  flow through inline approval bubbles).
- Empty-placeholder "New chat" sessions and the "always have at least
  one session" invariant.
- Apple-green palette (`#7FBA3C`) → turquoise (`#40E0D0`).

### Architecture — host/shell split

Re-platformed the project around a host process and a thin IDE shell.
The Visual Studio extension is now a UI client that talks to a
standalone `ChatRelay.Host.exe` over JSON-RPC 2.0 (stdio framing,
LSP-style `Content-Length:` headers, `StreamJsonRpc`). All session,
adapter, MCP, settings, and permission logic moved out of the VSIX
into the host. This sets up the project for additional shells (Rider
plugin, VSCode extension, standalone app) without touching Core.

### Added
- `PROTOCOL.md` — wire contract (v0). Methods, notifications,
  shapes, error codes, cancellation semantics.
- New project layout under `src/`:
  - `ChatRelay.Core` (net10) — sessions, MCP, permissions, adapter
    registry, settings.
  - `ChatRelay.Adapter.ClaudeCli` / `.ClaudeApi` / `.Ollama` (net10)
    — one project per adapter, plug-in style.
  - `ChatRelay.Host` (net10) — JSON-RPC server hosting Core +
    adapters; published as a self-contained single-file exe and
    bundled into the VSIX.
  - `ChatRelay.Shell.Console` (net10) — smoke-test client that
    drives the host over stdio. Useful for catching protocol drift.
  - `ChatRelay.VisualStudio` (net48) — pure UI shell. Spawns the
    host on tool-window open, owns the host process for the
    extension's lifetime.
- Workspace concept first-class in the protocol. Sessions, project
  MCP config, and adapter CWD all hang off it. Updates dynamically
  on `SolutionEvents.Opened` / `.AfterClosing` so opening the chat
  pane before a solution loads still picks up the workspace later.
- Cross-IDE-ready session storage. Sessions are keyed by canonical
  workspace path; opening the same folder in any future IDE shell
  surfaces the same chat history.

### Changed
- `PermissionBroker` upgraded from net8 → net10.
- `JsonOptions` in `SessionStore` now case-insensitive on read;
  envelope reader accepts both `schemaVersion`/`Schemaversion`
  shapes for back-compat with existing on-disk files.
- VSIX no longer references Core / adapter assemblies — only
  `StreamJsonRpc` and a local `Protocol.cs` mirror of wire DTOs.
- 76 build warnings → 0 (real fixes + project-level `<NoWarn>` for
  threading-analyzer noise that didn't reflect bugs).

### Removed
- Stale `Architecture.md` and `TechnicalDetails.md` (~1500 lines of
  prose that would have gone stale with the rewire).
- `ChatRelay/Features/*` and `ChatRelay/Infrastructure/*` folder
  layouts — now flat per-concern under `Core/`.

### Fixed
- Sessions persist + reload across VS restarts (was silently
  empty-loading because the envelope reader was case-sensitive).
- Session label updates from "New chat" → first-prompt snippet.
- Workspace picked up correctly when chat pane opens before
  solution finishes loading.
- Session bleeding when switching mid-stream — chunks for the
  background session are now dropped from rendering, but still
  persisted on the host.
- Active file auto-attaches as a reference on send (was missing
  after the rewire's first cut).
- Reference merging (range coalescing + whole-file supersession)
  restored to backup behavior.

## [0.1.0] — 2026-04-23

Initial public release.

### Added
- Dockable chat tool window for Visual Studio 2022 (17.14+) and
  Visual Studio 2026 (Community / Professional / Enterprise).
- Multi-backend adapter layer with three built-in adapters:
  **Claude CLI** (subscription-based, via the `claude` CLI),
  **Claude API** (BYOK via `ANTHROPIC_API_KEY`), and
  **Ollama** (local, via `http://localhost:11434`).
  All three auto-detected at startup; surviving backends contribute
  their models to one grouped dropdown.
- **Cross-adapter Model Context Protocol runtime** (`IMcpRuntime`).
  Configure MCP servers once and they work with every backend —
  Claude CLI delegates via `--mcp-config`, Claude API and Ollama
  drive the tool-use loop in-process.
- MCP tool-gate popup (next to the Send button) with per-tool and
  per-server enable / disable checkboxes. Unchecked tools are
  hidden from the model; the server process itself keeps running.
- Dedicated `.chatrelay.mcp.json` format (`mcpServers` root, same
  convention as Claude Code's CLI). Filename intentionally disjoint
  from VS Copilot's `.mcp.json` so the two tools never collide over
  file ownership or override semantics.
- Inline permission bubbles for Claude CLI tool-use approvals,
  replacing the TTY prompt that would otherwise hang on redirected
  stdio. Backed by an out-of-process `ChatRelay.PermissionBroker.exe`.
- Editor integration: *Send Selection to Claude* (`Ctrl+Alt+S`)
  pins a code selection as a reference; whole-document selection
  is detected and pins as a whole-file reference instead.
- Solution Explorer integration: *Add to Claude Chat* pins one or
  more selected files as references; multi-select supported.
- Smart reference merging — overlapping selections of the same
  file coalesce; a whole-file reference supersedes pinned ranges.
- Reference chips with click-to-open, per-range navigation, and
  hover-X to drop individual ranges.
- Session continuity: Claude CLI sessions resume via `--resume`;
  stateless backends replay the full conversation from local
  storage. Sessions are scoped per-solution with SHA-1-hashed
  storage filenames.
- Versioned session-store envelope (`schemaVersion: 2`) so future
  field changes don't silently wipe chat history.
- Active file auto-attach (toggle in settings).
- Markdown rendering with VS-theme-aware syntax highlighting and
  per-code-block copy buttons.
- Per-session usage accounting (input / output / cache / cost),
  rolled up across multi-turn tool-use loops for API / Ollama.

### Security / robustness
- CLI argument injection fixed: every user-controlled value
  (paths, model ids, tool patterns) routes through a CRT-compliant
  quoting helper before reaching `ProcessStartInfo`.
- TLS protocol selection scoped to a per-client `HttpClientHandler`
  instead of the process-wide `ServicePointManager`, so ChatRelay
  doesn't stomp on other VS extensions' TLS config.
- Tool-call timeout (90 s per call) + max-iteration cap (10 rounds
  per turn) prevent runaway MCP servers from wedging a send.
- MCP server launch resolves PATHEXT and routes `.cmd` / `.bat`
  shims through `cmd.exe /c`, so dotnet global-tool MCP servers
  work out of the box.

### Known limitations
- HTTP / SSE MCP transports added in [Unreleased]; the original
  release shipped with stdio-only.
- Error List / Test Explorer integrations (*"Ask Claude about
  this error / failure"*) are planned but not yet shipped.
- No unit-test coverage yet.
- Tool call invocation isn't currently visualised in the chat
  stream — the model's final response appears with the tool
  results baked in, but the intermediate tool uses are not
  shown as bubbles.

[Unreleased]: https://github.com/Kyranio/ChatRelay/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Kyranio/ChatRelay/releases/tag/v0.1.2
[0.1.1]: https://github.com/Kyranio/ChatRelay/releases/tag/v0.1.1
[0.1.0]: https://github.com/Kyranio/ChatRelay/releases/tag/v0.1.0
