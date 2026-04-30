using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Bytes-level pipe between us and one MCP server. Knows nothing about
    /// JSON-RPC framing, the MCP protocol, or any handshake — just "send
    /// this string, raise an event for each string the server sent back."
    /// One implementation per transport family:
    /// <list type="bullet">
    ///   <item><see cref="StdioMcpTransport"/> — spawn a child process, talk over stdin/stdout.</item>
    ///   <item>(future) <c>HttpMcpTransport</c> — POST + SSE for remote servers.</item>
    /// </list>
    /// All MCP semantics (initialize handshake, tools/list, tools/call,
    /// id correlation) live in <see cref="McpServerHandle"/> on top of this
    /// abstraction, identical regardless of transport.
    /// <para>
    /// Adding a new transport = implement this interface + register it in
    /// <see cref="McpTransports.CreateFor"/>. Nothing else changes.
    /// </para>
    /// </summary>
    public interface IMcpTransport : IAsyncDisposable
    {
        /// <summary>Open the transport (launch process / open SSE channel). Throws on failure.</summary>
        Task ConnectAsync(CancellationToken ct);

        /// <summary>Send one JSON-RPC line to the server.</summary>
        Task SendLineAsync(string jsonLine, CancellationToken ct);

        /// <summary>
        /// Fired once for each JSON-RPC line read from the server. Empty
        /// lines are filtered by the transport. Subscribers run on a
        /// background thread; the handle does its own marshalling.
        /// </summary>
        event Action<string>? LineReceived;

        /// <summary>
        /// Fired exactly once when the transport breaks (process exit,
        /// pipe close, HTTP error). Argument is a best-effort reason
        /// string surfaced as the user-facing error message — stderr tail
        /// for stdio, HTTP body / status for remote, etc.
        /// </summary>
        event Action<string?>? Faulted;
    }
}
