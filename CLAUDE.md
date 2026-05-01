# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project shape

ChatRelay is a intermediary system that brings Claude (and other LLMs) into the IDE of choice via a dockable chat tool window.
Currently it only supports Visual Studio using a VSIX extension, but the architecture is designed to be transport- and host-agnostic, so it can be extended to other IDEs or even non-IDE hosts in the future. The core chat logic, session management, adapter contracts, and MCP runtime are all decoupled from the VS-specific shell.
It is split across **two processes** with a strict seam between them:

- **`ChatRelay.VisualStudio`** — the VSIX shell. `net48`, WPF, runs in-proc inside `devenv`. Contains UI only.
- **`ChatRelay.Host.exe`** — `net10.0` self-contained single-file exe, published into the VSIX at build time and spawned by the shell on first chat-window open. Owns sessions, adapters, MCP, permissions, settings.

The two communicate over **JSON-RPC 2.0 on stdio with LSP-style framing** (`Content-Length` headers). The shell holds **zero** references to host-side code — only to `ChatRelay.Contracts` (the wire DTOs, `netstandard2.0`). When you change a wire shape, change it in `ChatRelay.Contracts/Protocol.cs` and both sides pick it up.

A third process, **`ChatRelay.PermissionBroker.exe`**, is spawned by the Claude CLI itself when it needs tool-use approval; it relays the request through a named pipe to the host, which surfaces it in chat as an `onPermissionRequest`. Keep it as a separate self-contained exe — inlining it back into the host causes the CLI's stdio prompts to hang.

The protocol is documented in `PROTOCOL.md`; layered diagrams are in `ARCHITECTURE.md`. Read those before extending the wire surface.

## Build & test

The solution file is `ChatRelay.slnx` (XML solution format — VS 2022 17.10+ / VS 2026).

```bash
# Full build via MSBuild (CI uses this exact sequence)
MSBuild.exe ChatRelay.slnx /t:Restore /p:Configuration=Release /v:minimal
MSBuild.exe ChatRelay.slnx /t:Rebuild  /p:Configuration=Release /v:minimal

# Integration tests — must run with --no-build after a Release rebuild,
# because the test fixture loads the host DLL from its own build config.
dotnet test src/ChatRelay.IntegrationTests/ChatRelay.IntegrationTests.csproj -c Release --no-build

# Single test
dotnet test src/ChatRelay.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~ListAdapters_returns_array"
```

Built VSIX lands at `src/ChatRelay.VisualStudio/bin/Release/net48/ChatRelay.vsix`. The shell project's `.csproj` has two `BeforeBuild` targets that publish `ChatRelay.Host` and `ChatRelay.PermissionBroker` self-contained for `win-x64` and copy them into the VSIX — you don't need to run those publishes manually.

F5 from VS launches the Experimental Instance with the extension loaded (`launchSettings.json` is preconfigured to load the solution itself, so editor + Solution Explorer commands have real code to bind to).

### Integration test config trap

`HostFixture.cs` derives the host DLL path from `AppContext.BaseDirectory` and pulls the build config out of the path. Running `dotnet test -c Release` against a `Debug` host build (or vice versa) silently loads the wrong DLL. Always rebuild and test with the same `-c` flag — CI does this; mirror it locally.

### .NET runtime trap (VS 2026)

VS 2026 ships **both** .NET 8 and .NET 10 runtimes, but the in-proc extension host resolves to .NET 8. The VSIX itself is `net48` (Framework, not Core), so it doesn't hit this. The host and broker target `net10.0` but ship **self-contained**, so they bring their own runtime. **Don't change either to a non-self-contained net10 publish** — the silent-load failure mode wastes hours.

## Install (VS 2026 only)

Double-clicking the `.vsix` on VS 2026 silently does nothing. Install requires admin PowerShell + `setup.exe modify --vsix ...`. Don't pass `--quiet` if it appears to do nothing — the real error only surfaces without it. README has the exact command.

## Architectural conventions

### Adapter and transport plug-ins

Two extension seams, both implemented as separate projects so they compose without touching the rest of the code:

- **AI backend** → new `ChatRelay.Adapter.<Name>` project implementing `IAiAdapter` (`ChatRelay.Core/Backends/IAiAdapter.cs`). Register it in `ChatRelay.Host`'s startup via `registry.Register(new MyAdapter())`. Model dropdown, session persistence, MCP tool dispatch, the tool-gate UI, and reference handling all hook in automatically.
- **MCP transport** → implement `IMcpTransport` (`ChatRelay.Core/Mcp/IMcpTransport.cs`) and add to the selector in `McpTransports.cs`.

### Backend defaults — order matters

Three adapters coexist; the shell auto-detects them on startup. The intended priority is **Claude CLI (subscription) → Claude API (BYOK) → Ollama**. The CLI is the default because it billed via the user's subscription; BYOK is fallback; Ollama is local-only extras. Don't reorder probe priority without the user explicitly asking — that's the entire product positioning.

### Where to look

| Looking for | Start at |
|---|---|
| Wire format spec | `PROTOCOL.md` |
| Wire DTOs | `ChatRelay.Contracts/Protocol.cs`, `Settings.cs`, `McpConfig.cs` |
| JSON-RPC method handlers | `ChatRelay.Host/HostService.cs` |
| Adapter contract | `ChatRelay.Core/Backends/IAiAdapter.cs` |
| MCP runtime | `ChatRelay.Core/Mcp/IMcpRuntime.cs` |
| Sessions persistence | `ChatRelay.Sessions/SessionStore.cs` |
| Settings persistence | `ChatRelay.Settings/SettingsStore.cs` |
| Chat state + logic | `ChatRelay.VisualStudio/Chat/ChatViewModel.cs` |
| Chat rendering | `ChatRelay.VisualStudio/Chat/ChatControl.xaml.cs` |
| Logs on disk | `%LocalAppData%\ChatRelay\logs\extension-YYYY-MM-DD.log` |
| Sessions on disk | `%LocalAppData%\ChatRelay\sessions\<sha1>.json` |
| Settings on disk | `%LocalAppData%\ChatRelay\settings.json` |

### MCP filename convention

Project / global config files are **`.chatrelay.mcp.json`**, deliberately distinct from VS Copilot's `.mcp.json`. Don't migrate to the bare name — keeping them disjoint is what stops the two tools from fighting over file ownership.

## Working in this repo

- The shell project is WPF on net48, so a handful of VSTHRD analyzer warnings are silenced in `ChatRelay.VisualStudio.csproj` (`VSSDK007;VSTHRD010;VSTHRD100;VSTHRD101;VSTHRD200`). Don't reintroduce those at call sites — the suppressions are intentional given the analyzer's inability to trace UI-thread call sites through the toolkit.
- Before writing code against any VS SDK surface, **read the installed NuGet DLL or check Microsoft.VisualStudio.SDK source rather than recalling from memory** — the SDK shape changes between versions and prior hallucinations have cost real time here.
- CI builds + runs integration tests on every PR into `dev` or `master` (`.github/workflows/ci.yml`). The `Release` workflow tags + publishes a GitHub Release whenever `source.extension.vsixmanifest`'s `Version` moves to a value not already tagged on origin.

## Contribution flow

- direct push blocked on both dev and master branches
- feature → dev (forks fine), dev → master for promotion
- required checks: build-and-test on dev, plus check-source on master
- linear history required (squash or rebase merges only)
- admin bypass off
- conventional commit prefixes for clean release notes

## Releasing & versioning

- manifest bump = ship gesture; release CI keys off it
- 3-part semver, pre-1.0 until protocol & VS extension stable
- dedicated release/v<version> PRs; fold [Unreleased] per Keep a Changelog
- never bump version in feature PRs
