using System.Collections.Concurrent;
using System.Text;
using ChatRelay.Backends;
using ChatRelay.Changes;
using ChatRelay.Chat;
using ChatRelay.Mcp;
using ChatRelay.Permissions;
using ChatRelay.Settings;
using StreamJsonRpc;

namespace ChatRelay.Host;

public sealed class HostService
{
    const string ProtocolVersion = "0";
    const string ServerName = "ChatRelay.Host";
    const string ServerVersion = "0.1.0";

    readonly AdapterRegistry _registry;
    readonly PermissionBrokerService _broker;
    readonly ChangeTracker _changes = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pendingPermissions = new();
    string? _workspace;

    public JsonRpc? Rpc { get; set; }
    public string BrokerPipeName => _broker.PipeName;

    public HostService(AdapterRegistry registry, PermissionBrokerService broker)
    {
        _registry = registry;
        _broker = broker;
        _broker.RequestReceived = OnBrokerRequestAsync;
        McpRuntimeHost.Instance.Servers.CollectionChanged += (_, _) => BroadcastServersChanged();

        // Push a fresh snapshot to any listening shell on every state mutation.
        _changes.Notify = (sessionId, snapshot) =>
            _ = Rpc?.NotifyAsync("onChangesUpdated", new ChangesUpdatedEvent(snapshot));
    }

    // Lifecycle ------------------------------------------------------------

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public async Task<InitializeResult> InitializeAsync(InitializeParams p, CancellationToken ct)
    {
        if (p.ProtocolVersion != ProtocolVersion)
            throw new LocalRpcException($"Protocol version mismatch: client={p.ProtocolVersion} server={ProtocolVersion}") { ErrorCode = -32001 };
        _workspace = p.WorkspacePath;
        _changes.WorkspaceRoot = _workspace;
        McpRuntimeHost.Instance.Refresh(_workspace);
        _ = McpRuntimeHost.Instance.EnsureServersStartedAsync(CancellationToken.None);
        await _registry.RefreshAsync(ct);
        return new InitializeResult(ServerName, ServerVersion, ProtocolVersion);
    }

    [JsonRpcMethod("shutdown")]
    public Task ShutdownAsync() => Task.CompletedTask;

    [JsonRpcMethod("setWorkspace", UseSingleObjectParameterDeserialization = true)]
    public Task SetWorkspaceAsync(SetWorkspaceParams p)
    {
        _workspace = p.Path;
        _changes.WorkspaceRoot = _workspace;
        McpRuntimeHost.Instance.Refresh(_workspace);
        _ = McpRuntimeHost.Instance.EnsureServersStartedAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    // Adapters + models ----------------------------------------------------

    [JsonRpcMethod("listAdapters")]
    public IReadOnlyList<AdapterInfo> ListAdapters() =>
        _registry.AvailableAdapters.Select(a => new AdapterInfo(a.Id, a.DisplayName, true)).ToList();

    [JsonRpcMethod("refreshAdapters")]
    public async Task RefreshAdaptersAsync(CancellationToken ct)
    {
        await _registry.RefreshAsync(ct);
        _ = Rpc?.NotifyAsync("onAdaptersChanged");
        _ = Rpc?.NotifyAsync("onModelsChanged");
    }

    [JsonRpcMethod("listModels")]
    public IReadOnlyList<ModelSummary> ListModels() =>
        _registry.Models.Select(m => new ModelSummary(m.Id, m.AdapterId, m.DisplayName)).ToList();

    // Sessions -------------------------------------------------------------

    [JsonRpcMethod("listSessions")]
    public IReadOnlyList<SessionSummary> ListSessions() =>
        SessionStore.Load(_workspace)
            // Index BEFORE filtering so the SessionId we hand back still
            // matches the on-disk position (openSession looks up by index).
            .Select((s, i) => (Session: s, Index: i))
            // Hide sessions with no user / assistant turns. A session is
            // only "real" once it has at least one exchange — the home
            // state and any send-failure orphans never show up. Existing
            // empty-placeholder pollution from earlier bugs is filtered
            // out automatically.
            .Where(t => t.Session.Messages.Any(m => m.Kind is BubbleKind.User or BubbleKind.Assistant))
            .Select(t => new
            {
                t.Index,
                t.Session,
                LastAt = t.Session.Messages
                    .Where(m => m.Timestamp.HasValue)
                    .Select(m => m.Timestamp!.Value)
                    .DefaultIfEmpty()
                    .Max(),
            })
            // Newest-first by last message timestamp. Sessions without any
            // timestamped messages (legacy persisted format) sort to the
            // end via DateTime.MinValue.
            .OrderByDescending(x => x.LastAt)
            .Select(x => new SessionSummary(
                x.Index.ToString(),
                x.Session.Label,
                x.Session.AdapterId,
                x.Session.ModelId,
                x.LastAt == default ? (DateTime?)null : x.LastAt))
            .ToList();

    [JsonRpcMethod("openSession", UseSingleObjectParameterDeserialization = true)]
    public OpenSessionResult OpenSession(OpenSessionParams p)
    {
        var sessions = SessionStore.Load(_workspace);
        var index = int.TryParse(p.SessionId, out var i) ? i : -1;
        if (index < 0 || index >= sessions.Count)
        {
            var fresh = new PersistedSession { Label = "New chat" };
            sessions.Add(fresh);
            SessionStore.Save(_workspace, sessions);
            var newId = (sessions.Count - 1).ToString();
            SessionStore.SetActiveSession(_workspace, newId);
            return new OpenSessionResult(newId, null, null, null, Array.Empty<SessionMessage>());
        }
        // Track the most recently opened session so the next launch can
        // restore the user's last position instead of dumping them on the
        // oldest one.
        SessionStore.SetActiveSession(_workspace, index.ToString());
        var ps = sessions[index];
        var messages = ps.Messages
            .Where(m => m.Kind is BubbleKind.User or BubbleKind.Assistant)
            .Select(m => new SessionMessage(
                m.Kind == BubbleKind.User ? "user" : "assistant",
                m.Text,
                m.Thinking,
                m.Usage is null ? null : new UsagePayload(
                    m.Usage.InputTokens, m.Usage.OutputTokens,
                    m.Usage.CacheReadTokens, m.Usage.CacheWriteTokens,
                    m.Usage.CostUsd),
                m.Model,
                m.Timestamp))
            .ToList();
        return new OpenSessionResult(index.ToString(), ps.AdapterId, ps.ModelId, ps.DraftText, messages);
    }

    [JsonRpcMethod("getActiveSession")]
    public string? GetActiveSession() => SessionStore.GetActiveSession(_workspace);

    [JsonRpcMethod("setSessionDraft", UseSingleObjectParameterDeserialization = true)]
    public Task SetSessionDraftAsync(SetSessionDraftParams p)
    {
        var sessions = SessionStore.Load(_workspace);
        if (int.TryParse(p.SessionId, out var i) && i >= 0 && i < sessions.Count)
        {
            sessions[i].DraftText = p.Text ?? string.Empty;
            SessionStore.Save(_workspace, sessions);
        }
        return Task.CompletedTask;
    }

    [JsonRpcMethod("deleteSession", UseSingleObjectParameterDeserialization = true)]
    public Task DeleteSessionAsync(DeleteSessionParams p)
    {
        var sessions = SessionStore.Load(_workspace);
        if (int.TryParse(p.SessionId, out var i) && i >= 0 && i < sessions.Count)
        {
            sessions.RemoveAt(i);
            SessionStore.Save(_workspace, sessions);
        }
        return Task.CompletedTask;
    }

    // Turn -----------------------------------------------------------------

    [JsonRpcMethod("sendPrompt", UseSingleObjectParameterDeserialization = true)]
    public Task<SendPromptResult> SendPromptAsync(SendPromptParams p)
    {
        var adapter = _registry.GetById(p.AdapterId)
            ?? throw new LocalRpcException($"Adapter not available: {p.AdapterId}") { ErrorCode = -32003 };

        var cts = new CancellationTokenSource();
        _inflight[p.SessionId] = cts;

        _ = Task.Run(() => RunTurnAsync(adapter, p, cts));
        return Task.FromResult(new SendPromptResult(true));
    }

    async Task RunTurnAsync(IAiAdapter adapter, SendPromptParams p, CancellationTokenSource cts)
    {
        var finalText = new StringBuilder();
        var finalThinking = new StringBuilder();
        AiUsage? finalUsage = null;
        string? assignedSession = null;
        // Last ModelInfo we saw — written onto the assistant bubble so the
        // history shows the actual concrete model (e.g. "claude-sonnet-4-5")
        // instead of the picker label ("Sonnet" / "Default").
        string? finalModel = null;

        void OnMsg(object? _, AiMessageEvent e)
        {
            switch (e.Kind)
            {
                case AiEventKind.AssistantMessage:
                    finalText.Append(e.Content);
                    _ = Rpc?.NotifyAsync("onAssistantChunk", new AssistantChunkParams(p.SessionId, e.Content ?? ""));
                    break;
                case AiEventKind.ThinkingMessage:
                    finalThinking.Append(e.Content);
                    _ = Rpc?.NotifyAsync("onThinkingChunk", new ThinkingChunkParams(p.SessionId, e.Content ?? ""));
                    break;
                case AiEventKind.ModelInfo:
                    if (!string.IsNullOrEmpty(e.ModelDisplayName)) finalModel = e.ModelDisplayName;
                    // Tell the change tracker which model is currently
                    // driving this session so it can stamp it on every
                    // tool_use that follows. Surfaced in the snapshot's
                    // HunkInfo.Model for the editor's "Edited by X" tooltip.
                    _changes.SetCurrentModel(p.SessionId, e.ModelDisplayName);
                    _ = Rpc?.NotifyAsync("onModelInfo", new ModelInfoEvent(p.SessionId, e.ModelDisplayName ?? ""));
                    break;
                case AiEventKind.SessionUpdate:
                    assignedSession = e.SessionId;
                    _ = Rpc?.NotifyAsync("onSessionIdAssigned", new SessionIdAssignedParams(p.SessionId, e.SessionId ?? ""));
                    break;
                case AiEventKind.UsageUpdate when e.Usage is not null:
                    finalUsage = e.Usage;
                    _ = Rpc?.NotifyAsync("onUsage", new UsageParams(
                        p.SessionId,
                        e.Usage.InputTokens, e.Usage.OutputTokens,
                        e.Usage.CacheReadTokens, e.Usage.CacheWriteTokens,
                        e.Usage.CostUsd));
                    break;
            }
        }

        void OnErr(object? _, AiErrorEvent e) =>
            _ = Rpc?.NotifyAsync("onError", new ErrorEvent(p.SessionId, e.Message));

        // Route every observed tool call to the change tracker. The tracker
        // filters to file-mutating tools and to paths inside the workspace,
        // so unrelated traffic (Read/Grep/Glob, mcp__*, etc.) is dropped
        // cheaply. Updates fire onChangesUpdated through ChangeTracker.Notify.
        void OnTool(object? _, ToolCallObservedEvent e) =>
            _changes.Observe(p.SessionId, new ToolCallObservation
            {
                ToolName = e.ToolName,
                InputJson = e.InputJson,
                Phase = e.Phase == ChatRelay.Backends.ToolCallPhase.Requested
                    ? ChatRelay.Changes.ToolCallPhase.Requested
                    : ChatRelay.Changes.ToolCallPhase.Completed,
            });

        adapter.MessageReceived += OnMsg;
        adapter.ErrorReceived += OnErr;
        adapter.ToolCallObserved += OnTool;

        var cancelled = false;
        try
        {
            await adapter.SendPromptAsync(BuildRequest(p, adapter), cts.Token);
            PersistTurn(p, finalText.ToString(), finalThinking.ToString(), finalUsage, assignedSession, adapter.Id, finalModel);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex)
        {
            await (Rpc?.NotifyAsync("onError", new ErrorEvent(p.SessionId, ex.Message)) ?? Task.CompletedTask);
        }
        finally
        {
            adapter.MessageReceived -= OnMsg;
            adapter.ErrorReceived -= OnErr;
            adapter.ToolCallObserved -= OnTool;
            _inflight.TryRemove(p.SessionId, out _);
            await (Rpc?.NotifyAsync("onTurnDone", new TurnDoneParams(p.SessionId, cancelled)) ?? Task.CompletedTask);
        }
    }

    [JsonRpcMethod("cancelTurn", UseSingleObjectParameterDeserialization = true)]
    public async Task CancelTurnAsync(CancelTurnParams p)
    {
        if (_inflight.TryGetValue(p.SessionId, out var cts)) await cts.CancelAsync();
    }

    // Settings -------------------------------------------------------------

    [JsonRpcMethod("getSettings")]
    public ExtensionSettings GetSettings() => SettingsStore.Load();

    [JsonRpcMethod("updateSettings", UseSingleObjectParameterDeserialization = true)]
    public ExtensionSettings UpdateSettings(UpdateSettingsParams p)
    {
        SettingsStore.Save(p.Patch);
        return SettingsStore.Load();
    }

    // MCP ------------------------------------------------------------------

    [JsonRpcMethod("listMcpServers")]
    public IReadOnlyList<McpServerSummary> ListMcpServers() =>
        McpRuntimeHost.Instance.Servers.Select(ToServerSummary).ToList();

    [JsonRpcMethod("startMcpServer", UseSingleObjectParameterDeserialization = true)]
    public async Task StartMcpServerAsync(McpServerIdParams p)
    {
        var handle = FindMcpServer(p.Id);
        if (handle is not null) await handle.StartAsync();
    }

    [JsonRpcMethod("stopMcpServer", UseSingleObjectParameterDeserialization = true)]
    public async Task StopMcpServerAsync(McpServerIdParams p)
    {
        var handle = FindMcpServer(p.Id);
        if (handle is not null) await handle.StopAsync();
    }

    [JsonRpcMethod("restartMcpServer", UseSingleObjectParameterDeserialization = true)]
    public async Task RestartMcpServerAsync(McpServerIdParams p)
    {
        var handle = FindMcpServer(p.Id);
        if (handle is not null) await handle.RestartAsync();
    }

    [JsonRpcMethod("setMcpServerEnabled", UseSingleObjectParameterDeserialization = true)]
    public Task SetMcpServerEnabledAsync(SetMcpServerEnabledParams p)
    {
        var s = SettingsStore.Load();
        var list = s.Permissions.DisabledMcpServers;
        if (p.Enabled) list.Remove(p.Id); else if (!list.Contains(p.Id)) list.Add(p.Id);
        SettingsStore.Save(s);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("setMcpToolEnabled", UseSingleObjectParameterDeserialization = true)]
    public Task SetMcpToolEnabledAsync(SetMcpToolEnabledParams p)
    {
        var s = SettingsStore.Load();
        var id = $"mcp__{p.ServerId}__{p.ToolName}";
        var list = s.Permissions.DisabledMcpTools;
        if (p.Enabled) list.Remove(id); else if (!list.Contains(id)) list.Add(id);
        SettingsStore.Save(s);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("listMcpFiles")]
    public IReadOnlyList<McpFileSummary> ListMcpFiles() =>
        SettingsStore.Load().McpFiles.Select(f => new McpFileSummary(f.FilePath, f.Scope.ToString())).ToList();

    [JsonRpcMethod("addMcpFile", UseSingleObjectParameterDeserialization = true)]
    public Task AddMcpFileAsync(AddMcpFileParams p)
    {
        var s = SettingsStore.Load();
        if (!s.McpFiles.Any(f => f.FilePath.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
        {
            s.McpFiles.Add(new TrackedMcpFile
            {
                FilePath = p.Path,
                Scope = Enum.TryParse<McpFileScope>(p.Scope, ignoreCase: true, out var scope) ? scope : McpFileScope.Global,
                ScopedSolutionPath = p.Scope.Equals("project", StringComparison.OrdinalIgnoreCase) ? _workspace : null,
            });
            SettingsStore.Save(s);
        }
        return Task.CompletedTask;
    }

    [JsonRpcMethod("removeMcpFile", UseSingleObjectParameterDeserialization = true)]
    public Task RemoveMcpFileAsync(RemoveMcpFileParams p)
    {
        var s = SettingsStore.Load();
        s.McpFiles.RemoveAll(f => f.FilePath.Equals(p.Path, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(s);
        return Task.CompletedTask;
    }

    // Permissions ----------------------------------------------------------

    [JsonRpcMethod("respondPermission", UseSingleObjectParameterDeserialization = true)]
    public Task RespondPermissionAsync(RespondPermissionParams p)
    {
        if (_pendingPermissions.TryRemove(p.RequestId, out var tcs))
        {
            tcs.TrySetResult(new PermissionDecision
            {
                Allow = p.Decision.Equals("allow", StringComparison.OrdinalIgnoreCase),
                AlwaysAllow = p.Remember,
            });
        }
        return Task.CompletedTask;
    }

    Task<PermissionDecision> OnBrokerRequestAsync(PermissionRequest req)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<PermissionDecision>();
        _pendingPermissions[requestId] = tcs;
        _ = Rpc?.NotifyAsync("onPermissionRequest",
            new PermissionRequestEvent(requestId, SessionId: "", req.ToolName, req.InputJson));
        return tcs.Task;
    }

    // Change tracking ------------------------------------------------------
    //
    // All operations are session-scoped. The tracker is volatile in-memory
    // state — a fresh host process (i.e. a fresh VS launch) starts empty.
    // Every mutating call here is a thin wrapper around ChangeTracker, which
    // also fires onChangesUpdated through the Notify hook set in the ctor.

    [JsonRpcMethod("listChanges", UseSingleObjectParameterDeserialization = true)]
    public SessionChangesSnapshot ListChanges(ListChangesParams p) =>
        _changes.Snapshot(p.SessionId);

    [JsonRpcMethod("acceptChange", UseSingleObjectParameterDeserialization = true)]
    public Task AcceptChangeAsync(AcceptChangeParams p)
    {
        _changes.Accept(p.SessionId, p.FilePath);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("denyChange", UseSingleObjectParameterDeserialization = true)]
    public Task DenyChangeAsync(DenyChangeParams p)
    {
        _changes.Deny(p.SessionId, p.FilePath);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("acceptHunk", UseSingleObjectParameterDeserialization = true)]
    public Task AcceptHunkAsync(AcceptHunkParams p)
    {
        _changes.AcceptHunk(p.SessionId, p.FilePath, p.BaselineStart, p.BaselineCount);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("rejectHunk", UseSingleObjectParameterDeserialization = true)]
    public Task RejectHunkAsync(RejectHunkParams p)
    {
        _changes.RejectHunk(p.SessionId, p.FilePath, p.BaselineStart, p.BaselineCount);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("redoDeniedChange", UseSingleObjectParameterDeserialization = true)]
    public Task RedoDeniedChangeAsync(RedoDeniedChangeParams p)
    {
        _changes.RedoDenial(p.SessionId, p.FilePath, p.DenialId);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("acceptAllOpenChanges", UseSingleObjectParameterDeserialization = true)]
    public Task AcceptAllOpenChangesAsync(BulkChangesParams p)
    {
        _changes.AcceptAllOpen(p.SessionId);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("denyAllOpenChanges", UseSingleObjectParameterDeserialization = true)]
    public Task DenyAllOpenChangesAsync(BulkChangesParams p)
    {
        _changes.DenyAllOpen(p.SessionId);
        return Task.CompletedTask;
    }

    [JsonRpcMethod("countOpenChanges", UseSingleObjectParameterDeserialization = true)]
    public int CountOpenChanges(BulkChangesParams p) => _changes.CountOpen(p.SessionId);

    // Helpers --------------------------------------------------------------

    static McpServerSummary ToServerSummary(McpServerHandle h) => new(
        h.Name, h.Name,
        h.Status.ToString().ToLowerInvariant(),
        h.ErrorMessage,
        h.IsGlobal ? "global" : "project",
        h.Tools.Select(t => new McpToolSummary(t.Name, t.Description)).ToList());

    static McpServerHandle? FindMcpServer(string id) =>
        McpRuntimeHost.Instance.Servers.FirstOrDefault(s => s.Name.Equals(id, StringComparison.Ordinal));

    void BroadcastServersChanged()
    {
        foreach (var h in McpRuntimeHost.Instance.Servers)
            _ = Rpc?.NotifyAsync("onMcpServerChanged", ToServerSummary(h));
    }

    AiRequest BuildRequest(SendPromptParams p, IAiAdapter adapter)
    {
        var prompt = new StringBuilder();
        if (p.References is not null)
        {
            foreach (var r in p.References)
            {
                var item = new ReferenceItem { FilePath = r.Path, AbsolutePath = r.Path, FullContent = r.FullContent };
                if (r.Ranges is not null)
                    foreach (var range in r.Ranges)
                        item.Ranges.Add(new LineRange { Start = range.Start, End = range.End });
                item.AppendToPrompt(prompt);
            }
        }
        prompt.Append(p.Text);

        // Pull the persisted session in one shot — both stateful adapters
        // (Claude CLI, via SessionId/--resume) and stateless ones (ClaudeApi,
        // Ollama; via History) read from the same source of truth. Reading
        // session id from disk per turn is what makes server-side resume
        // actually work; previously this came from a per-turn variable that
        // hadn't been populated yet by the time BuildRequest was called.
        var sessions = SessionStore.Load(_workspace);
        PersistedSession? persisted = null;
        if (int.TryParse(p.SessionId, out var idx) && idx >= 0 && idx < sessions.Count)
            persisted = sessions[idx];

        var req = new AiRequest
        {
            Prompt = prompt.ToString(),
            History = HistoryFor(persisted),
            Model = p.ModelId,
            SessionId = persisted?.SessionId,
            Mcp = McpRuntimeHost.Instance,
        };

        if (adapter.Capabilities.PermissionModes)
        {
            var settings = SettingsStore.Load();
            req.WorkingDirectory = _workspace;
            req.AdditionalDirectories = settings.Permissions.AdditionalDirectories.ToList();
            req.AllowedTools = settings.Permissions.AllowedTools.ToList();
            req.DisallowedTools = BuildDisallowedTools(settings);

            var brokerExe = ResolveBrokerExePath();
            if (brokerExe is not null)
            {
                req.PermissionPromptTool = "mcp__cvs-permissions__approve";
                req.McpConfigPath = McpRuntimeHost.Instance.WriteMergedConfigFile(
                    _workspace, brokerExe, BrokerPipeName);
            }
            else
            {
                req.McpConfigPath = McpRuntimeHost.Instance.WriteMergedConfigFile(_workspace);
            }
        }

        return req;
    }

    static List<string> BuildDisallowedTools(ExtensionSettings settings)
    {
        var result = new List<string>(settings.Permissions.DisallowedTools);
        result.AddRange(settings.Permissions.DisabledMcpTools);
        foreach (var server in settings.Permissions.DisabledMcpServers)
        {
            var handle = McpRuntimeHost.Instance.Servers.FirstOrDefault(h => h.Name == server);
            if (handle is null || handle.Tools.Count == 0) { result.Add($"mcp__{server}"); continue; }
            foreach (var t in handle.Tools) result.Add($"mcp__{server}__{t.Name}");
        }
        return result;
    }

    static string? ResolveBrokerExePath()
    {
        var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var candidate = System.IO.Path.Combine(dir!, "ChatRelay.PermissionBroker.exe");
        return System.IO.File.Exists(candidate) ? candidate : null;
    }

    static IReadOnlyList<AiTurn> HistoryFor(PersistedSession? s)
    {
        if (s is null) return Array.Empty<AiTurn>();
        return s.Messages
            .Where(m => m.Kind is BubbleKind.User or BubbleKind.Assistant)
            .Select(m => new AiTurn
            {
                Role = m.Kind == BubbleKind.User ? AiTurnRole.User : AiTurnRole.Assistant,
                Content = m.Text
            })
            .ToList();
    }

    void PersistTurn(SendPromptParams p, string assistantText, string thinking, AiUsage? usage, string? remoteSessionId, string adapterId, string? model)
    {
        var sessions = SessionStore.Load(_workspace);
        if (!int.TryParse(p.SessionId, out var i) || i < 0 || i >= sessions.Count) return;
        var s = sessions[i];
        s.AdapterId = adapterId;
        s.ModelId = p.ModelId;
        if (remoteSessionId is not null) s.SessionId = remoteSessionId;
        if (string.IsNullOrEmpty(s.Label) || s.Label == "New chat") s.Label = Truncate(p.Text, 40);

        s.Messages.Add(new PersistedBubble { Kind = BubbleKind.User, Text = p.Text, Timestamp = DateTime.UtcNow });
        s.Messages.Add(new PersistedBubble
        {
            Kind = BubbleKind.Assistant,
            Text = assistantText,
            Thinking = thinking.Length == 0 ? null : thinking,
            Usage = usage is null ? null : new PersistedUsage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CacheReadTokens = usage.CacheReadTokens,
                CacheWriteTokens = usage.CacheWriteTokens,
                CostUsd = usage.CostUsd,
            },
            Timestamp = DateTime.UtcNow,
            Model = model,
        });

        SessionStore.Save(_workspace, sessions);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
