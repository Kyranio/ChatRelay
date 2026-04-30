using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Settings;
using StreamJsonRpc;

namespace ChatRelay.Host;

public sealed class HostClient : IDisposable
{
    readonly HostProcess _proc;
    readonly JsonRpc _rpc;

    public event Action<AssistantChunkParams>? AssistantChunk;
    public event Action<ThinkingChunkParams>? ThinkingChunk;
    public event Action<ModelInfoEvent>? ModelInfo;
    public event Action<SessionIdAssignedParams>? SessionIdAssigned;
    public event Action<UsageParams>? Usage;
    public event Action<ErrorEvent>? Error;
    public event Action<TurnDoneParams>? TurnDone;
    public event Action<PermissionRequestEvent>? PermissionRequest;
    public event Action<McpServerSummary>? McpServerChanged;
    public event Action? AdaptersChanged;
    public event Action? ModelsChanged;

    public static HostClient Start()
    {
        var proc = HostProcess.Start();
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        var handler = new HeaderDelimitedMessageHandler(proc.Stdin, proc.Stdout, formatter);
        var rpc = new JsonRpc(handler);
        var c = new HostClient(proc, rpc);
        c.WireNotifications();
        rpc.StartListening();
        return c;
    }

    HostClient(HostProcess proc, JsonRpc rpc)
    {
        _proc = proc;
        _rpc = rpc;
    }

    void WireNotifications()
    {
        _rpc.AddLocalRpcMethod("onAssistantChunk", new Action<AssistantChunkParams>(p => AssistantChunk?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onThinkingChunk", new Action<ThinkingChunkParams>(p => ThinkingChunk?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onModelInfo", new Action<ModelInfoEvent>(p => ModelInfo?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onSessionIdAssigned", new Action<SessionIdAssignedParams>(p => SessionIdAssigned?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onUsage", new Action<UsageParams>(p => Usage?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onError", new Action<ErrorEvent>(p => Error?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onTurnDone", new Action<TurnDoneParams>(p => TurnDone?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onPermissionRequest", new Action<PermissionRequestEvent>(p => PermissionRequest?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onMcpServerChanged", new Action<McpServerSummary>(p => McpServerChanged?.Invoke(p)));
        _rpc.AddLocalRpcMethod("onAdaptersChanged", new Action(() => AdaptersChanged?.Invoke()));
        _rpc.AddLocalRpcMethod("onModelsChanged", new Action(() => ModelsChanged?.Invoke()));
    }

    public Task<InitializeResult> InitializeAsync(string? workspacePath, CancellationToken ct = default) =>
        _rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize",
            new InitializeParams("ChatRelay.VisualStudio", "0.1.0", "0", workspacePath), ct);

    public Task SetWorkspaceAsync(string? path) =>
        _rpc.InvokeWithParameterObjectAsync("setWorkspace", new SetWorkspaceParams(path));

    public Task<IReadOnlyList<AdapterInfo>> ListAdaptersAsync() =>
        _rpc.InvokeAsync<IReadOnlyList<AdapterInfo>>("listAdapters");

    public Task RefreshAdaptersAsync() => _rpc.InvokeAsync("refreshAdapters");

    public Task<IReadOnlyList<ModelSummary>> ListModelsAsync() =>
        _rpc.InvokeAsync<IReadOnlyList<ModelSummary>>("listModels");

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync() =>
        _rpc.InvokeAsync<IReadOnlyList<SessionSummary>>("listSessions");

    /// <summary>Id of the session most recently opened in this workspace, or null.</summary>
    public Task<string?> GetActiveSessionAsync() => _rpc.InvokeAsync<string?>("getActiveSession");

    public Task<OpenSessionResult> OpenSessionAsync(string? sessionId) =>
        _rpc.InvokeWithParameterObjectAsync<OpenSessionResult>("openSession", new OpenSessionParams(sessionId));

    public Task DeleteSessionAsync(string sessionId) =>
        _rpc.InvokeWithParameterObjectAsync("deleteSession", new DeleteSessionParams(sessionId));

    public Task SetSessionDraftAsync(string sessionId, string text) =>
        _rpc.InvokeWithParameterObjectAsync("setSessionDraft", new SetSessionDraftParams(sessionId, text));

    public Task<SendPromptResult> SendPromptAsync(SendPromptParams p) =>
        _rpc.InvokeWithParameterObjectAsync<SendPromptResult>("sendPrompt", p);

    public Task CancelTurnAsync(string sessionId) =>
        _rpc.InvokeWithParameterObjectAsync("cancelTurn", new CancelTurnParams(sessionId));

    public Task<ExtensionSettings> GetSettingsAsync() => _rpc.InvokeAsync<ExtensionSettings>("getSettings");
    public Task<ExtensionSettings> UpdateSettingsAsync(ExtensionSettings patch) =>
        _rpc.InvokeWithParameterObjectAsync<ExtensionSettings>("updateSettings", new UpdateSettingsParams(patch));

    public Task<IReadOnlyList<McpServerSummary>> ListMcpServersAsync() =>
        _rpc.InvokeAsync<IReadOnlyList<McpServerSummary>>("listMcpServers");
    public Task StartMcpServerAsync(string id) => _rpc.InvokeWithParameterObjectAsync("startMcpServer", new McpServerIdParams(id));
    public Task StopMcpServerAsync(string id) => _rpc.InvokeWithParameterObjectAsync("stopMcpServer", new McpServerIdParams(id));
    public Task RestartMcpServerAsync(string id) => _rpc.InvokeWithParameterObjectAsync("restartMcpServer", new McpServerIdParams(id));
    public Task SetMcpServerEnabledAsync(string id, bool enabled) =>
        _rpc.InvokeWithParameterObjectAsync("setMcpServerEnabled", new SetMcpServerEnabledParams(id, enabled));
    public Task SetMcpToolEnabledAsync(string serverId, string toolName, bool enabled) =>
        _rpc.InvokeWithParameterObjectAsync("setMcpToolEnabled", new SetMcpToolEnabledParams(serverId, toolName, enabled));

    public Task<IReadOnlyList<McpFileSummary>> ListMcpFilesAsync() =>
        _rpc.InvokeAsync<IReadOnlyList<McpFileSummary>>("listMcpFiles");
    public Task AddMcpFileAsync(string path, string scope) =>
        _rpc.InvokeWithParameterObjectAsync("addMcpFile", new AddMcpFileParams(path, scope));
    public Task RemoveMcpFileAsync(string path) =>
        _rpc.InvokeWithParameterObjectAsync("removeMcpFile", new RemoveMcpFileParams(path));

    public Task RespondPermissionAsync(string requestId, string decision, bool remember) =>
        _rpc.InvokeWithParameterObjectAsync("respondPermission", new RespondPermissionParams(requestId, decision, remember));

    public async Task ShutdownAsync()
    {
        try { await _rpc.InvokeAsync("shutdown"); } catch { }
    }

    public void Dispose()
    {
        try { _rpc.Dispose(); } catch { }
        _proc.Dispose();
    }
}
