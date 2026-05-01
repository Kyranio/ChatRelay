# ChatRelay

A relay between IDEs and language-model chat services. ChatRelay puts a
dockable chat window inside the IDE and brokers messages through to the
LLM backend you've configured. Today only the Visual Studio extension
ships — the underlying host is shell-agnostic, so support for other
IDEs can be added by implementing the
[JSON-RPC protocol](docs/PROTOCOL.md).

## Visual Studio extension

Can be used with **Visual Studio 2022 (17.14+)** or **Visual Studio 2026**, any edition.

1. Download `ChatRelay.vsix` from the
   [Releases page](https://github.com/Kyranio/ChatRelay/releases).
2. Make sure Visual Studio is closed fully, before installing.
3. Run the installer by double-clicking the downloaded `.vsix` file.

### Usage
- Open the chat window via `Tools > Open ChatRelay`.
> The chat window will also open automatically upon any ChatRelay action inside Visual Studio.
- In any opened file, select some lines of code (or text), right-click and choose `Send selection to ChatRelay` to append the selection as a reference.
	- Multiple selections can be sent from multiple files. Selections can be removed individually.
- Inside the Solution Explorer, right-click on any file and choose `Send file to ChatRelay` to append the entire file as a reference.
- MCP servers can be configured within the settings page, accessible via the gearwheel button in the chat window.
	- Many configuration files can be configured, both globally and per-solution.
	- Tools can be turned on or off individually using the MCP tool menu, accessable via the MCP button in the chat window (next to the gearwheel).
- The model you chat with can be switched at any time using the model dropdown in the chat toolbar.
	- Models are loaded automatically upon startup. When using Ollama, make sure to have the daemon running and your models installed.

## Currently supported

**IDEs**
- Visual Studio 2022 (17.14+)
- Visual Studio 2026

**Language-model backends**
- [Claude CLI](https://docs.claude.com/en/docs/claude-code/overview) — uses your Anthropic subscription
- [Anthropic API](https://docs.anthropic.com/en/api/) — pay-per-token via `ANTHROPIC_API_KEY`
- [Ollama](https://ollama.com/) — local models, auto-discovered at `http://localhost:11434`

## Docs

- [Architecture](docs/ARCHITECTURE.md) — two-process design, layered diagrams.
- [Protocol](docs/PROTOCOL.md) — JSON-RPC wire contract between shell and host.
- [Development](docs/DEVELOPMENT.md) — building, testing, and running the host.
- [Contributing](docs/CONTRIBUTE.md) — branch flow, commit conventions, PR rules.

## License

[MIT](LICENSE) © Kyran Oostra.

MCP is a trademark of the Model Context Protocol project; the MCP icon
used in the chat toolbar is displayed for nominative identification of
the protocol this extension integrates with.
