# ChatRelay Architecture

Wire contract: [PROTOCOL.md](PROTOCOL.md). Recent changes: [CHANGELOG.md](CHANGELOG.md). End-user features: [README.md](README.md).

---

## 1. Two processes

```mermaid
flowchart LR
    subgraph IDE["Visual Studio (devenv, .NET 4.8)"]
        Shell["Shell (VSIX)<br/>chat tool window<br/>settings · editor commands"]
    end
    subgraph HostBox["ChatRelay.Host.exe (.NET 10, self-contained)"]
        Host["Host service"]
    end
    Broker["PermissionBroker.exe<br/>(spawned by Claude CLI)"]

    Shell <-- "JSON-RPC 2.0 over stdio" --> Host
    Host -.named pipe.-> Broker
```

VS hosts the VSIX in-proc on net48; everything else runs in a child net10 process. The shell has zero references to host-side code — the protocol is the only seam.

---

## 2. Layers

```mermaid
flowchart TB
    Shell["Shell tier · any UI client"]
    Wire["Wire DTOs · single source of truth"]
    Dispatch["JSON-RPC dispatcher"]
    subgraph Inside["Inside the host"]
        Frameworks["Frameworks · Backends · MCP"]
        Slices["Slices · Sessions · Settings · Permissions · Logging"]
    end
    Adapters[("Adapters · plug-in")]
    Transports[("MCP transports · plug-in")]

    Shell <--> Wire
    Wire --> Dispatch
    Dispatch --> Frameworks
    Dispatch --> Slices
    Frameworks --> Slices
    Frameworks --> Adapters
    Frameworks --> Transports
```

Wire DTOs live in one assembly referenced by both shell and host. The dispatcher routes calls to either a framework or a feature slice. Adapters and transports plug in without touching the rest.

---

## 3. Slices

Each slice is its own folder/project — open one to see everything that concept does.

```mermaid
flowchart LR
    Logging
    Settings --> Logging
    Sessions --> Logging
    Permissions --> Logging
    Settings -.shape.-> Wire["Wire DTOs"]
    Sessions -.shape.-> Wire
```

| Slice | Owns |
|-------|------|
| Logging | daily-file logger |
| Settings | `settings.json` load · save · migrate |
| Sessions | per-workspace chat persistence + reference items |
| Permissions | named-pipe server for CLI tool approvals |

Slices share the namespace with their wire DTOs — different assembly, no friction at the call site.

---

## 4. Adapters & MCP

```mermaid
flowchart LR
    subgraph Frameworks["Core frameworks"]
        AdapterIface["IAiAdapter"]
        Runtime["IMcpRuntime"]
        Handle["McpServerHandle<br/>(handshake + RPC correlation)"]
        TransportIface["IMcpTransport"]
        AdapterIface -.uses.-> Runtime
        Runtime --> Handle
        Handle --> TransportIface
    end

    Adapters["Adapters<br/>Claude CLI · Claude API · Ollama"]
    Transports["Transports<br/>stdio · http/sse"]

    Adapters -.implements.-> AdapterIface
    Transports -.implements.-> TransportIface
```

Two extension seams: a new AI implements `IAiAdapter`; a new MCP transport implements `IMcpTransport` and is added to the transport switch. Neither path touches anything else.

---

## 5. Shell internals

```mermaid
flowchart TB
    Pkg["VS package<br/>(extension entry point)"]

    subgraph Surface["User-facing surface"]
        Tool["Chat tool window"]
        Dlg["Settings dialog"]
        Cmd["Editor + Solution Explorer commands"]
    end

    subgraph ChatInside["Chat tool window"]
        VM["Chat view-model<br/>state + host calls + logic"]
        Control["Chat control<br/>rendering + threading"]
        McpMenu["MCP tool menu"]
        Md["Markdown renderer<br/>(theme-bound)"]
        Control -.subscribes to.-> VM
    end

    EditorSel["Editor selection (DTE)"]

    subgraph Bridge["Host bridge"]
        HostClient["Host client<br/>typed RPC + events"]
        HostProc["Host process owner"]
    end

    HostExe[("ChatRelay.Host.exe")]

    Pkg --> Tool
    Pkg --> Cmd
    Pkg --> Dlg
    Tool --> Control
    Control --> McpMenu
    Control --> Md
    Cmd --> EditorSel
    EditorSel --> Control
    Dlg --> HostClient
    VM --> HostClient
    McpMenu --> HostClient
    HostClient --> HostProc
    HostProc -.spawns + stdio.-> HostExe
```

Pure UI. Every data round-trip goes through `HostClient`. The VSIX has zero references to Core or to any slice — only the wire DTOs.

---

## 6. Persistence

```mermaid
flowchart LR
    subgraph Disk["%LocalAppData%\\ChatRelay\\"]
        S["settings.json"]
        Sess["sessions\\&lt;sha1&gt;.json"]
        Log["logs\\&lt;date&gt;.log"]
    end
    Workspace["Workspace path"] -- "SHA-1" --> Sess
```

All writes are atomic (tmp + rename); all reads tolerate missing files. Same canonical workspace path → same chats across any future IDE shell.

---

## 7. Chat states

```mermaid
stateDiagram-v2
    [*] --> Loading
    Loading --> Home: host + models ready
    Home --> Home: sessions arrive in background<br/>(recent list fills in)
    Home --> Active: auto-restore last-used<br/>(if user hasn't interacted)
    Home --> Active: pick from dropdown / recent list
    Home --> Active: send first message<br/>(session created on demand)
    Active --> Home: New Chat / delete current
    Active --> Busy: send
    Busy --> Active: turn done / cancel / error
```

Sessions only exist on disk once they've had at least one user/assistant turn — empty placeholders are filtered server-side. The loading overlay drops as soon as host + models are ready; the session list arrives shortly after via a background fetch. Bubble headers carry the actual model + timestamp persisted with each turn.

---

## 8. Sending a prompt

```mermaid
sequenceDiagram
    actor User
    participant Shell
    participant Host
    participant Adapter
    participant Disk

    User->>Shell: type + Send
    Shell->>Host: sendPrompt
    Host-->>Shell: { accepted }
    Note over Host: build request, run turn
    Host->>Adapter: SendPromptAsync
    loop streaming
        Adapter-->>Host: chunks · thinking · usage
        Host-->>Shell: onAssistantChunk · onThinkingChunk · onUsage
    end
    Adapter-->>Host: turn complete
    Host->>Disk: persist turn
    Host-->>Shell: onTurnDone
```

Cancellation flows the other way (`cancelTurn`). Permission requests interrupt this flow with an `onPermissionRequest` notification → `respondPermission` reply round-trip.

---

## 9. Where to look

| Looking for | Start at |
|-------------|----------|
| Wire format | `PROTOCOL.md` |
| Wire DTOs | `ChatRelay.Contracts/Protocol.cs` |
| Settings shape | `ChatRelay.Contracts/Settings.cs` |
| Adapter contract | `ChatRelay.Core/Backends/IAiAdapter.cs` |
| MCP runtime | `ChatRelay.Core/Mcp/IMcpRuntime.cs` |
| MCP transport contract | `ChatRelay.Core/Mcp/IMcpTransport.cs` |
| MCP transport selector | `ChatRelay.Core/Mcp/McpTransports.cs` |
| Sessions persistence | `ChatRelay.Sessions/SessionStore.cs` |
| Settings persistence | `ChatRelay.Settings/SettingsStore.cs` |
| JSON-RPC method handlers | `ChatRelay.Host/HostService.cs` |
| Chat state + logic | `ChatRelay.VisualStudio/Chat/ChatViewModel.cs` |
| Chat rendering | `ChatRelay.VisualStudio/Chat/ChatControl.xaml.cs` |
| Settings dialog | `ChatRelay.VisualStudio/Settings/SettingsWindow.xaml.cs` |
| Logs on disk | `%LocalAppData%\ChatRelay\logs\` |
| Sessions on disk | `%LocalAppData%\ChatRelay\sessions\` |
| Settings on disk | `%LocalAppData%\ChatRelay\settings.json` |
