# ChatRelay for Visual Studio

Chat with Claude, Ollama, and any other supported LLM from a dockable pane
inside Visual Studio — with Model Context Protocol (MCP) tools that work
across every backend, not just the Claude CLI.

![ChatRelay screenshot placeholder](docs/screenshot.png)

## Why

Most LLM integrations for Visual Studio force you to pick one provider and
stay there. ChatRelay is a relay, not a client — it auto-detects whichever
LLM runtimes you have installed and hands you one grouped model dropdown
covering all of them. Configure an MCP server once and it works with every
backend, not just the one Anthropic ships.

## Features

- **Dockable chat tool window** with markdown rendering, syntax-highlighted
  code blocks, copyable snippets, and VS-theme-aware styling (Dark / Light
  / Blue).
- **Three backends, auto-detected on startup** —
  [Claude CLI](https://docs.claude.com/en/docs/claude-code/overview) (your
  Anthropic subscription),
  [Anthropic API](https://docs.anthropic.com/en/api/) (via
  `ANTHROPIC_API_KEY`), and
  [Ollama](https://ollama.com/) (any local model). Each surviving probe
  contributes its models to one grouped dropdown.
- **Per-session backend.** Each chat remembers which adapter + model it was
  using; switching mid-conversation is fine.
- **Model Context Protocol, cross-adapter.** Configure MCP servers once and
  they're available to every backend — not just the Claude CLI. A tool-gate
  menu next to the Send button lets you toggle individual tools on or off
  per session without restarting the server.
- **Dedicated `.chatrelay.mcp.json` files.** Our own filename + schema
  (`mcpServers` root, same convention as Claude Code) — kept disjoint
  from VS Copilot's `.mcp.json` so the two tools never fight over who
  owns a file or which one overrides the other.
- **Send code as references.** Right-click a selection (`Ctrl+Alt+S`) or a
  Solution Explorer file to pin it to the next message. Whole-file and
  range-based references merge intelligently — overlapping selections of
  the same file coalesce.
- **Clickable navigation.** Click any reference chip to open the file; click
  a range to jump to that span. Hover a range to drop just that span.
- **Session continuity across restarts.** Per-solution JSON persistence with
  a versioned envelope. Claude CLI sessions resume via `--resume`;
  stateless backends replay the full conversation from local storage.
- **Permission broker.** Claude CLI tool-use approvals surface as inline
  bubbles in the chat (Allow once / Allow always / Deny) instead of TTY
  prompts that would hang on redirected stdio.
- **No vendor lock-in on conversations.** Session files live under
  `%LocalAppData%\ChatRelay\`; nothing leaves your machine unless you
  picked a cloud backend.

## Install

Requires **Visual Studio 2022 (17.14+)** or **Visual Studio 2026**
(Community, Professional, or Enterprise). The VSIX is a `net48` classic
extension; the host process it spawns runs on .NET 10 self-contained, so
you don't need .NET 10 installed system-wide.

1. Download `ChatRelay.vsix` from the
   [Releases page](https://github.com/Kyranio/ChatRelay/releases) (once
   published), or build it locally (see
   [Building from source](#building-from-source)).
2. Close Visual Studio.
3. **On Visual Studio 2026**, classic VSIXes need `setup.exe modify` with
   admin elevation — double-clicking the `.vsix` silently fails. In an
   **administrator** PowerShell:

   ```powershell
   & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" `
       modify `
       --installPath "C:\Program Files\Microsoft Visual Studio\18\Enterprise" `
       --vsix "C:\path\to\ChatRelay.vsix"
   ```

   Adjust the `--installPath` to match your VS edition (`Enterprise`,
   `Professional`, or `Community`). If the install appears to do nothing,
   drop `--quiet` to see the real error.

4. **On Visual Studio 2022**, double-clicking the `.vsix` works normally.
5. Start Visual Studio. Open **Tools → Open Claude Chat**. Configure at
   least one backend (below) and pick a model from the dropdown.

## Configure a backend

At least one of these must be available for the extension to do anything.
All three can coexist.

**Claude CLI (subscription billing, recommended)**
```bash
npm install -g @anthropic-ai/claude-code
claude login
```
Once `claude --version` works from a terminal, ChatRelay will detect it on
next startup. The CLI's own permission prompts route through an inline
approval bubble in the chat (no TTY hang).

**Anthropic API (pay-per-token)**
Set `ANTHROPIC_API_KEY` in your user environment variables. The extension
lists every model your account can access. No startup round-trip — a bad
key surfaces as a 401 on the first send.

**Ollama (local, free)**
Install [Ollama](https://ollama.com/), pull a model (`ollama pull llama3.1`
or similar with tool-calling support), and leave the daemon running.
Auto-discovered at `http://localhost:11434`; set `OLLAMA_HOST` to override.

## Configure MCP servers

ChatRelay reads its own `.chatrelay.mcp.json` files. The filename is
deliberately distinct from `.mcp.json` so VS Copilot / VS Code never try
to validate, override, or hint-button our files. Two scopes:

- **Global** — `%LocalAppData%\ChatRelay\.chatrelay.mcp.json`, shared across solutions
- **Project** — `.chatrelay.mcp.json` at the solution root (or repo root if in git)

Format is `{ "mcpServers": { "<name>": { "command": "…", "args": [...],
"env": {...} } } }` — same root key Claude Code's CLI uses, so if you're
migrating an existing `.mcp.json` you just rename it. Use **Tools →
Open Claude Chat → ⚙ → MCP Servers** to create either file in-place.

Once configured, click the **MCP** icon next to the settings gear to see
all available tools across every server, with per-tool and per-server
toggles. Unchecked tools are hidden from the model on the next send (the
server itself keeps running — unchecking just removes the tool from the
model's visible surface).

Tool execution works with every backend:

| Backend | How MCP tools run |
|---|---|
| Claude CLI | Delegated to the `claude` process via `--mcp-config` |
| Claude API | ChatRelay drives the tool-use loop itself (inject schemas → parse `tool_use` → dispatch → feed results back) |
| Ollama | Same, using Ollama's OpenAI-compatible function-calling dialect |

## Send code along with your question

- **Editor selection** → right-click → *Send Selection to Claude*
  (or `Ctrl+Alt+S`). Pins the selected lines as a reference chip.
- **Select all** (`Ctrl+A`) before sending pins the whole file instead.
- **Solution Explorer** → right-click a file → *Add to Claude Chat*.
  Multi-select works.
- **Active file auto-attach** is on by default — the visible document gets
  included as context on every send unless you've explicitly pinned
  something else. Toggle in settings.

Click any reference chip to open the file; click a specific range to jump
to that span. Hover a range to remove just that range.

## Building from source

```bash
git clone https://github.com/Kyranio/ChatRelay.git
cd ChatRelay
```

Open `ChatRelay.slnx` in Visual Studio and press **F5** to launch the
Experimental Instance with the extension loaded. The solution has
`.vscode/`-style `launchSettings.json` preconfigured to load the solution
itself into the Experimental Instance, so you immediately have real code
to test editor + Solution Explorer commands against.

Requirements for building:
- Visual Studio 2022 (17.14+) or 2026 with the **Visual Studio extension
  development** workload
- .NET SDK 10 (for the host + permission broker; the VSIX itself is net48)

Or from the command line:

```bash
MSBuild.exe ChatRelay.slnx /t:Rebuild /p:Configuration=Release
```

Built VSIX lands at `src/ChatRelay.VisualStudio/bin/Release/net48/ChatRelay.vsix`.

## Troubleshooting

If a backend you expected isn't in the model dropdown, check the log:

```
%LocalAppData%\ChatRelay\logs\extension-YYYY-MM-DD.log
```

Each probe logs its outcome ("no `ANTHROPIC_API_KEY`", "claude --version
timed out", "Ollama probe: connection refused", …). MCP server startup,
send-time requests, and tool dispatch all log there too.

## Architecture

ChatRelay is split into a **host process** and a **thin IDE shell**.
The Visual Studio extension is the shell — it owns the WPF UI and
spawns `ChatRelay.Host.exe` on first open. All session, adapter, MCP,
permission, and settings logic lives in the host. The two talk over
**JSON-RPC 2.0** on stdio (LSP-style framing).

```
src/
├── ChatRelay.Contracts/            netstandard2.0  — wire DTOs (single source of truth)
├── ChatRelay.Logging/              net10  — daily-file logger
├── ChatRelay.Settings/             net10  — settings.json load/save/migrate
├── ChatRelay.Sessions/             net10  — per-workspace chat persistence
├── ChatRelay.Permissions/          net10  — named-pipe server for CLI approvals
├── ChatRelay.Core/                 net10  — Backends + MCP frameworks
├── ChatRelay.Adapter.ClaudeCli/    net10  — one project per adapter, plug-in style
├── ChatRelay.Adapter.ClaudeApi/    net10
├── ChatRelay.Adapter.Ollama/       net10
├── ChatRelay.Host/                 net10  — JSON-RPC server, self-contained single-file exe
├── ChatRelay.Shell.Console/        net10  — smoke-test client
├── ChatRelay.IntegrationTests/     net10  — xUnit, asserts protocol contract
├── ChatRelay.PermissionBroker/     net10  — out-of-proc broker the Claude CLI calls
└── ChatRelay.VisualStudio/         net48  — UI shell, JSON-RPC client
```

For a deeper look, [ARCHITECTURE.md](ARCHITECTURE.md) walks the layers
diagrammatically.

The wire contract is documented in [PROTOCOL.md](PROTOCOL.md). Other
IDEs / standalone apps can implement the same protocol in their native
language and reuse the entire host.

### Adding an LLM backend

1. Add a new `ChatRelay.Adapter.<Name>` project (netstandard2.0 / net10).
2. Implement `IAiAdapter` (`Id`, `DisplayName`, `IsAvailableAsync`,
   `ListModelsAsync`, `SendPromptAsync` + the message events).
3. Register it in `ChatRelay.Host`'s startup:
   `registry.Register(new MyAdapter());`
4. Done. Model dropdown, session persistence, MCP tool dispatch, the
   tool-gate UI, and reference handling all work automatically.

### Adding a new IDE shell

Reuse the host as-is. Implement the JSON-RPC client in your IDE's native
plugin runtime (Kotlin for Rider, TypeScript for VSCode, …). No .NET
required on the shell side. See `PROTOCOL.md` for the contract.

## License

[MIT](LICENSE) © Kyran Oostra.

MCP is a trademark of the Model Context Protocol project; the MCP icon
used in the chat toolbar is displayed for nominative identification of
the protocol this extension integrates with.
