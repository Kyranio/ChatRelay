using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Settings;
using ChatRelay.Logging;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Default <see cref="IMcpRuntime"/>. Wraps an <see cref="McpServerManager"/>
    /// so the tool-menu UI and the send-time tool-use loop share one set of
    /// running server processes. Also centralises the qualified-id format
    /// (<c>mcp__server__tool</c>) every adapter and the disabled-tools UI
    /// rely on.
    /// </summary>
    public sealed class McpRuntime : IMcpRuntime
    {
        // Prefix + delimiter from the Claude CLI's tool-id convention. Using
        // the same format everywhere means the existing DisabledMcpTools
        // list, the CLI's --disallowedTools patterns, and API/Ollama tool
        // schemas all speak the same language.
        private const string Prefix = "mcp__";
        private const string Delim = "__";

        // Hard ceiling per tool call so a runaway MCP server can't wedge a
        // turn forever. 90s is generous for typical read/web/filesystem
        // operations; can be raised if a user reports a legitimately slow
        // tool.
        private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(90);

        private readonly McpServerManager _manager = new McpServerManager();
        private bool _disposed;

        public ObservableCollection<McpServerHandle> Servers => _manager.Servers;

        public void Refresh(string? solutionDir)
        {
            if (_disposed) return;
            _manager.Refresh(solutionDir);
        }

        public async Task EnsureServersStartedAsync(CancellationToken ct)
        {
            if (_disposed) return;
            var pending = _manager.Servers
                .Where(s => s.Status == McpServerStatus.Stopped
                            // Respect explicit user stops — if the user
                            // killed this server from the settings UI,
                            // don't silently bring it back to life on the
                            // next menu open or send. Only StartAsync
                            // (re-)enables it.
                            && !s.UserStopped)
                .ToList();
            if (pending.Count == 0) return;

            // Parallel start — each server handshake is IO-bound and most
            // of the wall clock is the child process's own initialisation.
            await Task.WhenAll(pending.Select(s =>
                Task.Run(async () =>
                {
                    try { await s.StartAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        ExtensionLogger.Warn("mcp-runtime", "Start failed: " + s.Name, ex);
                    }
                }, ct))).ConfigureAwait(false);
        }

        public IReadOnlyList<McpToolDescriptor> ListAvailableTools()
        {
            if (_disposed) return Array.Empty<McpToolDescriptor>();

            var settings = SettingsStore.Load();
            var disabledTools = new HashSet<string>(
                settings.Permissions?.DisabledMcpTools ?? new List<string>(),
                StringComparer.Ordinal);
            var disabledServers = new HashSet<string>(
                settings.Permissions?.DisabledMcpServers ?? new List<string>(),
                StringComparer.Ordinal);

            var list = new List<McpToolDescriptor>();
            foreach (var server in _manager.Servers)
            {
                if (server.Status != McpServerStatus.Running) continue;
                if (disabledServers.Contains(server.Name)) continue;

                foreach (var tool in server.Tools)
                {
                    var id = MakeToolId(server.Name, tool.Name);
                    if (disabledTools.Contains(id)) continue;

                    list.Add(new McpToolDescriptor(
                        server.Name, tool.Name, id, tool.Description, tool.InputSchema));
                }
            }
            return list;
        }

        public (string Server, string Tool)? TryParseToolId(string qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId)) return null;
            if (!qualifiedId.StartsWith(Prefix, StringComparison.Ordinal)) return null;

            // Split off the prefix, then find the FIRST "__" separator. Tool
            // names routinely contain underscores ("read_file"); server
            // names shouldn't contain "__" by convention, so splitting on
            // the first occurrence is safe for normal cases.
            var afterPrefix = qualifiedId.Substring(Prefix.Length);
            var sep = afterPrefix.IndexOf(Delim, StringComparison.Ordinal);
            if (sep <= 0 || sep + Delim.Length >= afterPrefix.Length) return null;

            var server = afterPrefix.Substring(0, sep);
            var tool = afterPrefix.Substring(sep + Delim.Length);
            return (server, tool);
        }

        public string MakeToolId(string server, string tool) =>
            Prefix + server + Delim + tool;

        public async Task<McpToolResult> CallToolAsync(
            string server, string tool, JsonElement arguments, CancellationToken ct)
        {
            if (_disposed)
                return McpToolResult.Error("MCP runtime has been disposed.");

            var handle = _manager.Servers.FirstOrDefault(s =>
                string.Equals(s.Name, server, StringComparison.Ordinal));
            if (handle == null)
                return McpToolResult.Error($"No MCP server named '{server}' is configured.");

            // Start on demand: a send can arrive before the user has ever
            // opened the tool menu. Keep the runtime's "running once, cached
            // thereafter" model intact. User-stopped servers stay stopped
            // — the model sees a clear "not running" error instead of the
            // server silently coming back to life.
            if (handle.Status == McpServerStatus.Stopped && !handle.UserStopped)
            {
                try { await handle.StartAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("mcp-runtime",
                        "Start-on-demand failed for " + server, ex);
                    return McpToolResult.Error("Failed to start MCP server: " + ex.Message);
                }
            }

            if (handle.Status != McpServerStatus.Running)
            {
                return McpToolResult.Error(
                    $"MCP server '{server}' is not running (status: {handle.Status}).");
            }

            return await handle.CallToolAsync(tool, arguments, ToolCallTimeout, ct)
                .ConfigureAwait(false);
        }

        public string? WriteMergedConfigFile(
            string? solutionDir, string? brokerExePath = null, string? brokerPipeName = null)
        {
            return McpConfigService.WriteMergedTempFile(solutionDir, brokerExePath, brokerPipeName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Fire-and-forget shutdown — Dispose is called from synchronous
            // UI events (control Unloaded), so we can't await. Each transport's
            // DisposeAsync has a bounded grace period before SIGKILL, so
            // the worker task isn't long-lived.
            _ = Task.Run(async () =>
            {
                try { await _manager.StopAllAsync().ConfigureAwait(false); }
                catch (Exception ex) { ExtensionLogger.Warn("mcp-runtime", "StopAll during Dispose", ex); }
            });
        }
    }
}
