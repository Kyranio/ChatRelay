using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Cross-adapter MCP host. The Claude CLI adapter delegates tool
    /// execution to the external CLI (via <see cref="WriteMergedConfigFile"/>);
    /// other adapters (Claude API, Ollama) drive the tool-use loop
    /// themselves by pulling schemas from <see cref="ListAvailableTools"/>
    /// and dispatching calls through <see cref="CallToolAsync"/>.
    ///
    /// One instance per chat tool-window: lifetime is owned by
    /// <c>ChatControl</c> and shared with <c>McpToolMenu</c> so the
    /// UI's visibility connection and the adapters' send-time tool calls
    /// hit the same running server processes.
    /// </summary>
    public interface IMcpRuntime : IDisposable
    {
        /// <summary>Live server handles — observable so the tool menu can reactively rebuild on changes.</summary>
        System.Collections.ObjectModel.ObservableCollection<McpServerHandle> Servers { get; }

        /// <summary>
        /// Re-read the tracked-file registry for the current solution
        /// context. Stops servers that are no longer configured and
        /// surfaces any newly-added ones in <see cref="Servers"/>. Does
        /// NOT start anything — use <see cref="EnsureServersStartedAsync"/>
        /// for that.
        /// </summary>
        void Refresh(string? solutionDir);

        /// <summary>
        /// Start any server currently in <see cref="McpServerStatus.Stopped"/>.
        /// Idempotent — already-running servers are untouched. Runs all
        /// start-ups in parallel so a slow server doesn't block the others.
        /// </summary>
        Task EnsureServersStartedAsync(CancellationToken ct);

        /// <summary>
        /// Tools exposed to the LLM on the next turn: flattened across
        /// every running server, filtered by the user's disabled-tools /
        /// disabled-servers selections from the MCP menu, qualified with
        /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> ids the adapters then
        /// translate back to (server, tool) when the model calls them.
        /// </summary>
        IReadOnlyList<McpToolDescriptor> ListAvailableTools();

        /// <summary>
        /// Split a qualified id back into (server, tool). Returns null
        /// when the id doesn't have the <c>mcp__</c> prefix (e.g. a
        /// built-in non-MCP tool the model invented or a string we
        /// didn't emit).
        /// </summary>
        (string Server, string Tool)? TryParseToolId(string qualifiedId);

        /// <summary>Compose a qualified id. Matches the format Claude CLI's <c>--disallowedTools</c> patterns use.</summary>
        string MakeToolId(string server, string tool);

        /// <summary>
        /// Invoke a tool. Any failure — server missing, timeout, tool
        /// error — is folded into a non-throwing <see cref="McpToolResult"/>
        /// with <see cref="McpToolResult.IsError"/> set, so the calling
        /// adapter can feed the message back to the LLM without aborting
        /// the whole turn. Cancellation does propagate.
        /// </summary>
        Task<McpToolResult> CallToolAsync(
            string server, string tool, JsonElement arguments, CancellationToken ct);

        /// <summary>
        /// Claude CLI path: assemble the merged <c>.chatrelay.mcp.json</c> (global +
        /// project + optionally the permission broker) and write it to a
        /// temp file the CLI can consume via <c>--mcp-config</c>. Returns
        /// null when there are no servers to pass through. Caller owns the
        /// returned path's cleanup.
        /// </summary>
        string? WriteMergedConfigFile(
            string? solutionDir, string? brokerExePath = null, string? brokerPipeName = null);
    }

    /// <summary>
    /// Flat view of one MCP-advertised tool, with the qualified id
    /// adapters emit into their own tool schemas. Tool descriptors are
    /// snapshots — regenerate via <see cref="IMcpRuntime.ListAvailableTools"/>
    /// after server starts / stops / refresh.
    /// </summary>
    public sealed class McpToolDescriptor
    {
        public string ServerName { get; }
        public string ToolName { get; }
        public string QualifiedId { get; }
        public string? Description { get; }
        public JsonElement InputSchema { get; }

        public McpToolDescriptor(
            string serverName, string toolName, string qualifiedId,
            string? description, JsonElement inputSchema)
        {
            ServerName = serverName;
            ToolName = toolName;
            QualifiedId = qualifiedId;
            Description = description;
            InputSchema = inputSchema;
        }
    }
}
