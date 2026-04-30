using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Settings;
using ChatRelay.Logging;

namespace ChatRelay.Mcp
{
    public enum McpServerStatus
    {
        Stopped,
        Starting,
        Running,
        Error
    }

    /// <summary>
    /// One configured MCP server, tracked by the settings window's server
    /// list. Lifecycle: Stopped → Starting → Running (or Error). The
    /// manager doesn't keep the process alive forever — the user toggles
    /// it from the UI. This is a *visibility* connection only; the Claude
    /// CLI still spawns its own subprocess per-send, independently.
    /// <para>
    /// Bytes flow over an <see cref="IMcpTransport"/> picked by
    /// <see cref="McpTransports.CreateFor"/>. The handle owns the
    /// MCP semantics (initialize handshake, tools/list, tools/call,
    /// id correlation); the transport owns the actual pipe (process,
    /// HTTP, …). Adding a new transport family doesn't touch this class.
    /// </para>
    /// </summary>
    public class McpServerHandle : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }
        public McpServerEntry Config { get; }

        /// <summary>Absolute path of the .chatrelay.mcp.json this server was loaded from.</summary>
        public string SourcePath { get; }

        /// <summary>True = global (<c>%LocalAppData%\ChatRelay\.chatrelay.mcp.json</c>), false = project (<c>&lt;solutionDir&gt;\.chatrelay.mcp.json</c>).</summary>
        public bool IsGlobal { get; }

        private McpServerStatus _status = McpServerStatus.Stopped;
        public McpServerStatus Status
        {
            get => _status;
            private set { if (_status != value) { _status = value; OnChanged(nameof(Status)); } }
        }

        private int _toolCount;
        public int ToolCount
        {
            get => _toolCount;
            private set { if (_toolCount != value) { _toolCount = value; OnChanged(nameof(ToolCount)); } }
        }

        private IReadOnlyList<McpToolInfo> _tools = Array.Empty<McpToolInfo>();
        /// <summary>
        /// Tools advertised by this server, as parsed from the last
        /// <c>tools/list</c> response. Empty until the server has been
        /// successfully started (or if the server has no tools). Stable
        /// across a running session — restart the server to re-poll.
        /// </summary>
        public IReadOnlyList<McpToolInfo> Tools
        {
            get => _tools;
            private set { _tools = value ?? Array.Empty<McpToolInfo>(); OnChanged(nameof(Tools)); }
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            private set { if (_errorMessage != value) { _errorMessage = value; OnChanged(nameof(ErrorMessage)); } }
        }

        private string? _statusDetail;
        /// <summary>
        /// Human-readable phase description during <see cref="McpServerStatus.Starting"/>
        /// ("Launching…", "Handshaking…", "Listing tools…"). UI renders
        /// this in place of the static "starting…" label so a slow
        /// startup doesn't feel like a hang. Cleared on success.
        /// </summary>
        public string? StatusDetail
        {
            get => _statusDetail;
            private set { if (_statusDetail != value) { _statusDetail = value; OnChanged(nameof(StatusDetail)); } }
        }

        private bool _userStopped;
        /// <summary>
        /// True when the user explicitly stopped this server via the UI
        /// (settings window or similar). Honored by
        /// <see cref="McpRuntime.EnsureServersStartedAsync"/> — servers
        /// with this flag set won't auto-restart when the tool menu
        /// opens or when a send happens, so the user's intent is
        /// preserved. Cleared when <see cref="StartAsync"/> is called
        /// (explicit start = un-stop).
        /// </summary>
        public bool UserStopped
        {
            get => _userStopped;
            private set { if (_userStopped != value) { _userStopped = value; OnChanged(nameof(UserStopped)); } }
        }

        private bool _isOverride;
        /// <summary>
        /// True when this server entry was loaded from the winning file
        /// in an override situation — e.g. both the project and global
        /// <c>.chatrelay.mcp.json</c> define a server with the same name,
        /// and this handle is the project one (project beats global).
        /// Drives the "overrides global" hint the settings UI renders
        /// next to the server name.
        /// </summary>
        public bool IsOverride
        {
            get => _isOverride;
            internal set { if (_isOverride != value) { _isOverride = value; OnChanged(nameof(IsOverride)); } }
        }

        private string? _shadowedSourcePath;
        /// <summary>
        /// When <see cref="IsOverride"/> is true, the absolute path of
        /// the file whose entry this handle overrides. Used by the
        /// settings UI to offer a "go to overridden copy" affordance
        /// if the user wants to edit the lower-priority entry.
        /// </summary>
        public string? ShadowedSourcePath
        {
            get => _shadowedSourcePath;
            internal set { if (_shadowedSourcePath != value) { _shadowedSourcePath = value; OnChanged(nameof(ShadowedSourcePath)); } }
        }

        // ------------------------------------------------------------------
        // Wiring: transport + RPC correlation.
        // ------------------------------------------------------------------

        private IMcpTransport? _transport;
        private CancellationTokenSource? _cts;

        // Outstanding JSON-RPC requests keyed by id. OnLineReceived dispatches
        // each incoming response to the matching TCS. Unrecognised ids
        // (server-initiated notifications, orphaned late responses) are
        // dropped silently.
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending
            = new();

        // 1 is reserved for `initialize`, 2 for `tools/list`. Subsequent
        // tools/call requests get monotonically increasing ids.
        private int _nextRequestId = 100;

        // Deadline for synchronous handshake RPCs (initialize, tools/list).
        // The MCP spec doesn't mandate a max latency, but a server that takes
        // >10s to answer initialize is broken in practice — surfacing it as
        // an error is more useful than letting the UI sit on "Handshaking…".
        private static readonly TimeSpan RpcHandshakeDeadline = TimeSpan.FromSeconds(10);

        public McpServerHandle(string name, McpServerEntry config, string sourcePath, bool isGlobal)
        {
            Name = name;
            Config = config;
            SourcePath = sourcePath;
            IsGlobal = isGlobal;
        }

        // ------------------------------------------------------------------
        // Lifecycle.
        // ------------------------------------------------------------------

        public async Task StartAsync()
        {
            if (Status == McpServerStatus.Starting || Status == McpServerStatus.Running) return;

            ErrorMessage = null;
            StatusDetail = "Launching…";
            // Explicit start — clear any prior "user stopped this" flag so
            // subsequent auto-start attempts (menu open, next send) don't
            // skip it anymore.
            UserStopped = false;
            Status = McpServerStatus.Starting;

            try
            {
                _transport = McpTransports.CreateFor(Name, Config, SourcePath);
                _transport.LineReceived += OnLineReceived;
                _transport.Faulted += OnTransportFaulted;

                _cts = new CancellationTokenSource();
                await _transport.ConnectAsync(_cts.Token).ConfigureAwait(false);

                // MCP handshake: initialize → await response → notify
                // initialized → tools/list → parse tools. Each phase gets
                // its own label so the user sees motion even on a slow
                // startup; a hang in any phase eventually hits the deadline
                // and rolls to Error with a useful message.
                StatusDetail = "Handshaking…";
                await SendRpcAsync(1, "initialize", BuildInitializeParams(),
                                   RpcHandshakeDeadline, _cts.Token).ConfigureAwait(false);

                await _transport.SendLineAsync(BuildInitializedNotification(), _cts.Token)
                                .ConfigureAwait(false);

                StatusDetail = "Listing tools…";
                var listResult = await SendRpcAsync(2, "tools/list", null,
                                                    RpcHandshakeDeadline, _cts.Token).ConfigureAwait(false);

                var parsedTools = ParseTools(listResult);
                Tools = parsedTools;
                ToolCount = parsedTools.Count;

                StatusDetail = null;
                Status = McpServerStatus.Running;
                ExtensionLogger.Info("mcp-server:" + Name,
                    $"Started ({ToolCount} tools)");
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-server:" + Name, "Start failed", ex);
                ErrorMessage = ex.Message;
                StatusDetail = null;
                Status = McpServerStatus.Error;
                await DisposeTransportAsync().ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            await DisposeTransportAsync().ConfigureAwait(false);
            ToolCount = 0;
            Tools = Array.Empty<McpToolInfo>();
            StatusDetail = null;
            // Remember that this was a user-initiated stop so downstream
            // auto-start paths (tool menu open, send-time ensure-started)
            // skip this server until the user starts it again manually.
            UserStopped = true;
            Status = McpServerStatus.Stopped;
            ExtensionLogger.Info("mcp-server:" + Name, "Stopped");
        }

        public async Task RestartAsync()
        {
            // StopAsync awaits the transport's actual shutdown via
            // DisposeAsync — by the time it returns the underlying pipe
            // (process / connection / etc.) is fully torn down, so
            // StartAsync can immediately respawn without racing the
            // previous instance.
            await StopAsync().ConfigureAwait(false);
            await StartAsync().ConfigureAwait(false);
        }

        // Tear the transport down deterministically and fail any pending
        // RPC waiters. Used by Stop, Restart, and the catch path of Start.
        private async Task DisposeTransportAsync()
        {
            try { _cts?.Cancel(); } catch { }

            var t = _transport;
            if (t != null)
            {
                t.LineReceived -= OnLineReceived;
                t.Faulted -= OnTransportFaulted;
                try { await t.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("mcp-server:" + Name,
                        "Transport DisposeAsync threw", ex);
                }
            }
            _transport = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;

            // Fail any caller still waiting on a response so they don't
            // hang forever after the transport went away.
            foreach (var kv in _pending.ToArray())
            {
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new InvalidOperationException("Server stopped."));
            }
        }

        // ------------------------------------------------------------------
        // Transport event handlers.
        // ------------------------------------------------------------------

        // Invoked from the transport's reader loop for each non-empty line.
        // Parses, looks up by id, completes the matching TCS. Bad lines and
        // unmatched ids are dropped quietly so a chatty server can't break
        // request/response routing.
        private void OnLineReceived(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl)) return;
                if (idEl.ValueKind != JsonValueKind.Number) return;
                if (!idEl.TryGetInt32(out var id)) return;

                if (!_pending.TryRemove(id, out var tcs)) return;

                if (root.TryGetProperty("error", out var errEl))
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "MCP error: " + errEl.GetRawText()));
                }
                else if (root.TryGetProperty("result", out var resultEl))
                {
                    // Clone so the JsonElement outlives the JsonDocument
                    // we're about to dispose at scope exit.
                    tcs.TrySetResult(resultEl.Clone());
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "MCP response missing result and error: " + line));
                }
            }
            catch (JsonException)
            {
                // Bad line — log and continue; doesn't affect other
                // pending requests.
                ExtensionLogger.Debug("mcp-server:" + Name, "Non-JSON line: " + line);
            }
        }

        // Transport broke (process died, HTTP closed, etc.). Roll the
        // handle into Error if we were running, and fail any pending
        // waiters so they unblock.
        private void OnTransportFaulted(string? reason)
        {
            if (Status == McpServerStatus.Running || Status == McpServerStatus.Starting)
            {
                ErrorMessage = string.IsNullOrEmpty(reason)
                    ? "Transport faulted."
                    : reason;
                StatusDetail = null;
                Status = McpServerStatus.Error;
            }

            foreach (var kv in _pending.ToArray())
            {
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new InvalidOperationException(
                        "Transport faulted: " + (reason ?? "(unknown)")));
            }
        }

        // ------------------------------------------------------------------
        // MCP method calls.
        // ------------------------------------------------------------------

        /// <summary>
        /// Invoke a tool by name. Wraps MCP's <c>tools/call</c> JSON-RPC
        /// round-trip. Any failure — server not running, timeout, protocol
        /// error, tool-reported error — is folded into a non-throwing
        /// <see cref="McpToolResult"/> so the calling adapter can surface
        /// the error to the model without aborting the whole turn.
        /// </summary>
        public async Task<McpToolResult> CallToolAsync(
            string toolName, JsonElement arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (Status != McpServerStatus.Running || _transport == null)
                return McpToolResult.Error("Server is not running.");

            var id = Interlocked.Increment(ref _nextRequestId);
            try
            {
                var result = await SendRpcAsync(
                    id, "tools/call", BuildToolCallParams(toolName, arguments),
                    timeout, ct).ConfigureAwait(false);
                return ParseToolResult(result);
            }
            catch (TimeoutException)
            {
                return McpToolResult.Error($"Tool '{toolName}' did not respond within {timeout}.");
            }
            catch (OperationCanceledException)
            {
                throw; // propagate — caller is shutting down
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-server:" + Name,
                    "tools/call " + toolName + " failed", ex);
                return McpToolResult.Error(ex.Message);
            }
        }

        // Issue a JSON-RPC request, register a pending-response TCS, and
        // await OnLineReceived's completion. The deadline is enforced via
        // a linked CancellationTokenSource: caller's cancel and the per-
        // call deadline collapse into a single token, no orphan timer
        // task. A timeout surfaces as TimeoutException; caller-driven
        // cancel surfaces as OperationCanceledException, distinguishing
        // the two.
        private async Task<JsonElement> SendRpcAsync(
            int id, string method, string? paramsJson, TimeSpan timeout, CancellationToken ct)
        {
            var transport = _transport
                ?? throw new InvalidOperationException("MCP transport is not connected.");

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
                throw new InvalidOperationException("Duplicate JSON-RPC id " + id);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                var frame = BuildJsonRpcRequest(id, method, paramsJson);
                await transport.SendLineAsync(frame, ct).ConfigureAwait(false);

                using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    try
                    {
                        return await tcs.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Disambiguate: caller cancel vs. our deadline.
                        if (ct.IsCancellationRequested) throw;
                        throw new TimeoutException(
                            $"MCP {method} (id {id}) did not respond within {timeout}.");
                    }
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ------------------------------------------------------------------
        // JSON-RPC frame builders + MCP response parsers (pure, transport-
        // agnostic).
        // ------------------------------------------------------------------

        // Single-line framing for one JSON-RPC request. paramsJson can be
        // null (no params), a bare object literal, or pre-built JSON.
        private static string BuildJsonRpcRequest(int id, string method, string? paramsJson)
        {
            var sb = new StringBuilder("{\"jsonrpc\":\"2.0\",\"id\":");
            sb.Append(id);
            sb.Append(",\"method\":");
            sb.Append(JsonSerializer.Serialize(method));
            if (!string.IsNullOrEmpty(paramsJson))
            {
                sb.Append(",\"params\":");
                sb.Append(paramsJson);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildInitializeParams()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("protocolVersion", "2024-11-05");
                w.WriteStartObject("capabilities");
                w.WriteEndObject();
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "ChatRelay");
                w.WriteString("version", "1.0.0");
                w.WriteEndObject();
                w.WriteEndObject();
                w.Flush();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string BuildInitializedNotification()
            => "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

        private static string BuildToolCallParams(string toolName, JsonElement arguments)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("name", toolName);
                w.WritePropertyName("arguments");
                // MCP expects an object for arguments. Fall back to {} for
                // anything else so the spec's contract is satisfied.
                if (arguments.ValueKind == JsonValueKind.Object)
                    arguments.WriteTo(w);
                else
                {
                    w.WriteStartObject();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.Flush();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // Flatten a tools/call result into something the adapters can hand
        // back to the model as a single string. Spec allows text / image /
        // resource content blocks; we surface text verbatim, describe
        // images, and emit a compact representation of resources.
        private static McpToolResult ParseToolResult(JsonElement result)
        {
            var isError = result.TryGetProperty("isError", out var errEl)
                          && errEl.ValueKind == JsonValueKind.True;

            if (!result.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return new McpToolResult { IsError = isError, Content = string.Empty };
            }

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String) continue;

                var type = typeEl.GetString();
                if (type == "text"
                    && block.TryGetProperty("text", out var txt)
                    && txt.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(txt.GetString());
                }
                else if (type == "image")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    var mime = block.TryGetProperty("mimeType", out var m) ? m.GetString() : "image";
                    sb.Append($"[image: {mime}]");
                }
                else if (type == "resource")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[resource: ").Append(block.GetRawText()).Append("]");
                }
            }
            return new McpToolResult { IsError = isError, Content = sb.ToString() };
        }

        // Walks a tools/list `result` object, producing one McpToolInfo
        // per advertised tool. Tool entries missing a name are silently
        // skipped; everything else (description, inputSchema) is optional
        // and defaults sensibly when absent. JsonElements are cloned so
        // the returned list outlives the caller's JsonDocument.
        private static IReadOnlyList<McpToolInfo> ParseTools(JsonElement result)
        {
            var list = new List<McpToolInfo>();
            if (!result.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array) return list;

            foreach (var t in tools.EnumerateArray())
            {
                if (!t.TryGetProperty("name", out var nameEl)
                    || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;

                string? description = null;
                if (t.TryGetProperty("description", out var descEl)
                    && descEl.ValueKind == JsonValueKind.String)
                {
                    description = descEl.GetString();
                }

                // Carry the input schema through verbatim so adapters can
                // hand it straight to the model as the tool's JSON schema.
                // Default-valued JsonElement means "no schema advertised".
                JsonElement schema = default;
                if (t.TryGetProperty("inputSchema", out var schemaEl)
                    && schemaEl.ValueKind == JsonValueKind.Object)
                {
                    schema = schemaEl.Clone();
                }

                list.Add(new McpToolInfo(name!, description, schema));
            }
            return list;
        }

        private void OnChanged(string prop)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Aggregates MCP server configs from the global .chatrelay.mcp.json +
    /// project .chatrelay.mcp.json into a single observable list the settings
    /// UI binds to. Call <see cref="Refresh"/> whenever the solution changes
    /// or a config file is edited.
    /// </summary>
    public class McpServerManager
    {
        public ObservableCollection<McpServerHandle> Servers { get; }
            = new ObservableCollection<McpServerHandle>();

        public string GlobalConfigPath => McpConfigService.GlobalConfigPath;
        public string? ProjectConfigPath { get; private set; }

        public event EventHandler? Changed;

        public void Refresh(string? solutionDir)
        {
            ProjectConfigPath = McpConfigService.GetProjectConfigPath(solutionDir);

            // Seed the tracked-file registry on first open so pre-registry
            // users don't see an empty list.
            var solutionPath = string.IsNullOrEmpty(solutionDir)
                ? null
                : McpConfigService.GuessSolutionPathFromDir(solutionDir);
            McpFileRegistry.EnsureSeeded(solutionPath);

            // Preserve running handles across refresh so the user doesn't
            // lose their live servers on a trivial refresh (e.g. settings
            // saved). Match by (source-file, name).
            var existing = Servers.ToList();
            var newList = new List<McpServerHandle>();

            // Iterate globals first, projects last, so project-scoped
            // entries overwrite global-scope ones on name conflict — same
            // rule WriteMergedTempFile uses for the CLI merge. Without this
            // dedup step the same server name coming from both the global
            // and project .chatrelay.mcp.json would render twice in the tool menu.
            var applicable = McpFileRegistry.ApplicableFor(solutionPath)
                .OrderBy(f => f.Scope == McpFileScope.Global ? 0 : 1)
                .ToList();

            // Indexed so the project-level override can REPLACE a previously
            // added global entry with the same server name.
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var tracked in applicable)
            {
                var parsed = McpConfigService.TryLoadFile(tracked.FilePath);
                if (parsed?.McpServers == null) continue;

                foreach (var kv in parsed.McpServers)
                {
                    // The permission-prompt broker is internal plumbing;
                    // never surface it in the user-facing list.
                    if (kv.Key == McpConfigService.PermissionBrokerServerName) continue;

                    var isGlobal = tracked.Scope == McpFileScope.Global;
                    var reused = existing.FirstOrDefault(h =>
                        h.Name == kv.Key
                        && string.Equals(h.SourcePath, tracked.FilePath, StringComparison.OrdinalIgnoreCase));
                    var handle = reused
                        ?? new McpServerHandle(kv.Key, kv.Value, tracked.FilePath, isGlobal);
                    // Reset override flags; the loop below re-populates
                    // them when a later file shadows an earlier one.
                    handle.IsOverride = false;
                    handle.ShadowedSourcePath = null;

                    if (nameIndex.TryGetValue(kv.Key, out var existingIdx))
                    {
                        // Project file overrides global. Record WHICH file
                        // got shadowed so the settings UI can show an
                        // "overrides global" hint next to the winning entry
                        // and offer a one-click jump to the loser if the
                        // user wants to edit it.
                        var shadowed = newList[existingIdx];
                        handle.IsOverride = true;
                        handle.ShadowedSourcePath = shadowed.SourcePath;
                        newList[existingIdx] = handle;
                    }
                    else
                    {
                        nameIndex[kv.Key] = newList.Count;
                        newList.Add(handle);
                    }
                }
            }

            // Stop any running servers that are no longer in the config.
            foreach (var gone in existing.Except(newList))
            {
                _ = gone.StopAsync();
            }

            Servers.Clear();
            foreach (var s in newList) Servers.Add(s);

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stop every running server — called when the tool window closes.</summary>
        public async Task StopAllAsync()
        {
            foreach (var s in Servers.ToList())
                await s.StopAsync().ConfigureAwait(false);
        }
    }
}
