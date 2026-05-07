# Development

This project mostly uses **.NET** and has been developed using **Visual Studio**. However, you can use any IDE or code editor that supports .NET development.
That said, if you want to work on an extension for a specific IDE, it's recommended to use that IDE for development to ensure compatibility and ease of testing.

Since this project uses a JSON-RPC based architecture, it is possible to develop new extensions in any language that can communicate over JSON-RPC, as long as it adheres to the defined protocol. 


### Good to know:
- Read about the [protocol](PROTOCOL.md) to understand how the core and extensions communicate.
- Check the [architecture](ARCHITECTURE.md) documentation to understand the overall structure of the project and how different components interact.
- When contributing, please follow the [contribution guidelines](CONTRIBUTE.md).

## Setting up the development environment
This is mostly up to your preferences and the specific part you want to work on.

### Visual Studio extension
You will need:
- Visual Studio with the "Visual Studio extension development" workload installed.
- .NET SDK compatible with the project, as of the time of writing, .NET 8.0 is used.
- As Visual Studio is only available on Windows, you would need a Windows machine for this too.

**Steps for set-up:**
1. Clone the (forked) repository and open the solution file in Visual Studio.
1. Make sure to restore NuGet packages and build the solution to ensure everything is set up correctly.
	1. Install any missing workloads/components if prompted by Visual Studio.
1. Make sure to set the correct startup project (`ChatRelay.VisualStudio`) to run the extension in the experimental instance of Visual Studio.
> **Note:** The experimental instance is a separate instance of Visual Studio used for testing extensions, so it won't affect your main Visual Studio environment.

### Core processes
The non-VSIX projects make up the core: a JSON-RPC host (`ChatRelay.Host`) that owns sessions, adapters, MCP, and settings, plus a permission broker (`ChatRelay.PermissionBroker`) that the Claude CLI shells out to for tool-use approvals. Any IDE shell that speaks the [protocol](PROTOCOL.md) over stdio can drive them — the host is shell-agnostic by design, so you don't need Visual Studio at all to work on this layer.

You will need:
- .NET SDK 10 — the host and broker target `net10.0` and ship **self-contained** for `win-x64` in Release builds, so end users don't need .NET 10 installed but you do.
- Any IDE or editor with .NET support, or just the `dotnet` CLI. JetBrains Rider and VS Code both work fine.
- Optional, only if you want to exercise a real backend end-to-end:
	- Node + the `@anthropic-ai/claude-code` CLI for the Claude CLI adapter.
	- An `ANTHROPIC_API_KEY` env var for the Claude API adapter.
	- A running [Ollama](https://ollama.com/) daemon for the Ollama adapter.

**Steps for set-up:**
1. Clone the (forked) repository.
1. Restore and build the solution: `dotnet build ChatRelay.slnx -c Debug`.
	1. Day-to-day iteration is faster on `Debug`; only switch to `Release` when you need the self-contained publish (e.g., to repackage the VSIX).
1. Pick how you want to drive the host:
	1. **Smoke-test client** — set `ChatRelay.Shell.Console` as the startup project. It spawns the host and lets you fire JSON-RPC methods interactively.
	1. **Integration tests** — `dotnet test src/ChatRelay.IntegrationTests -c Debug --no-build` runs the host under a fixture and asserts on the wire shape. Good for TDD on protocol changes.
	1. **Standalone** — `dotnet run --project src/ChatRelay.Host` launches the host directly with stdio framing; useful when you're writing a non-VS shell and want to talk to it from your own client.
1. Watch `%LocalAppData%\ChatRelay\logs\extension-YYYY-MM-DD.log` while iterating — every adapter probe, MCP server start, and tool dispatch lands there regardless of how the host was spawned.

> **Note:** The host is a stdio JSON-RPC server, not a UI app — it does nothing visible until a client connects and sends a request. The permission broker is stdio-only too and is launched by the Claude CLI itself; never run it directly, or the CLI's prompts will hang.
