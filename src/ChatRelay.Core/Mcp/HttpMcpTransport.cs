using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Streamable-HTTP transport for MCP per the 2025-03-26 spec: every
    /// JSON-RPC message is a POST to a single configured URL. The server
    /// responds with either <c>application/json</c> (single response) or
    /// <c>text/event-stream</c> (one or more responses streamed as SSE).
    /// We surface every response message — JSON-body single-shots and
    /// each <c>data:</c> SSE event alike — through <see cref="LineReceived"/>
    /// so the handle can correlate them by id without caring how they
    /// arrived.
    /// <para>
    /// Sticky session via the <c>Mcp-Session-Id</c> header — the server
    /// assigns it on initialize and we replay it on subsequent requests.
    /// On dispose we send a polite <c>DELETE</c> with the session id so
    /// the server can reclaim state.
    /// </para>
    /// <para>
    /// What this transport does NOT do (yet):
    /// </para>
    /// <list type="bullet">
    ///   <item>Standalone GET-SSE for server-initiated push (most public
    ///   MCP servers don't push unsolicited messages today).</item>
    ///   <item>The older 2024-11-05 "open SSE first then read endpoint
    ///   event" handshake.</item>
    ///   <item>Resumable streams (<c>Last-Event-ID</c>).</item>
    /// </list>
    /// Both <c>"type": "http"</c> and <c>"type": "sse"</c> map to this
    /// transport — they're configuration aliases, not different protocols.
    /// </summary>
    public sealed class HttpMcpTransport : IMcpTransport
    {
        private readonly string _serverName;
        private readonly Uri _endpoint;
        private readonly IReadOnlyDictionary<string, string>? _headers;
        private readonly HttpClient _client;

        private CancellationTokenSource? _cts;
        private string? _sessionId;
        private int _faulted;

        // Polite-shutdown deadline for the DELETE we send on dispose. Not
        // worth blocking the user's UI for an unreachable server.
        private static readonly TimeSpan SessionTerminateBudget = TimeSpan.FromSeconds(2);

        public event Action<string>? LineReceived;
        public event Action<string?>? Faulted;

        public HttpMcpTransport(
            string serverName,
            string url,
            IReadOnlyDictionary<string, string>? headers)
        {
            _serverName = serverName;
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException(
                    "HTTP MCP server requires a 'url' field.", nameof(url));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                throw new ArgumentException(
                    $"HTTP MCP server has an invalid 'url': {url}", nameof(url));
            _endpoint = parsed;
            _headers = headers;
            _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            // Streamable HTTP doesn't require a separate connect step —
            // the first POST establishes the session. We just stash a
            // linked CTS so background SSE drains can be cancelled on
            // dispose.
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return Task.CompletedTask;
        }

        public async Task SendLineAsync(string jsonLine, CancellationToken ct)
        {
            var transportToken = _cts?.Token ?? CancellationToken.None;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transportToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(jsonLine, Encoding.UTF8, "application/json"),
            };
            // Server picks single-shot JSON or streaming SSE based on the
            // request shape and its own preference; advertise that we
            // accept either.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!string.IsNullOrEmpty(_sessionId))
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId!);
            if (_headers != null)
            {
                foreach (var h in _headers)
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                FireFaulted("HTTP request failed: " + ex.Message);
                throw;
            }

            // Capture session id once the server emits one (typically on the
            // initialize round-trip). All subsequent requests replay it.
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sids))
            {
                var first = sids.FirstOrDefault();
                if (!string.IsNullOrEmpty(first)) _sessionId = first;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(resp, linked.Token).ConfigureAwait(false);
                var msg = $"HTTP {(int)resp.StatusCode}: {Truncate(body, 500)}";
                resp.Dispose();
                FireFaulted(msg);
                throw new HttpRequestException(msg);
            }

            // 202 / 204 = accepted notification, no body to drain.
            if (resp.StatusCode == HttpStatusCode.NoContent
                || resp.StatusCode == HttpStatusCode.Accepted)
            {
                resp.Dispose();
                return;
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Detached background drain — caller's SendLineAsync
                // returns immediately, the handle correlates inbound
                // events to its pending TCS by id whenever they arrive.
                // The transport cancellation token (set in ConnectAsync)
                // tears the stream down on Dispose.
                _ = Task.Run(() => DrainSseAsync(resp, transportToken));
            }
            else
            {
                try
                {
                    var body = await resp.Content.ReadAsStringAsync(linked.Token)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body))
                        FireLine(body.Trim());
                }
                finally
                {
                    resp.Dispose();
                }
            }
        }

        // Pull SSE events off the open response and forward each event's
        // data field as one line. Standard SSE framing — accumulate
        // consecutive `data: ...` lines, dispatch on blank line.
        private async Task DrainSseAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var dataBuf = new StringBuilder();
                string? line;
                while (!ct.IsCancellationRequested
                       && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0)
                    {
                        if (dataBuf.Length > 0)
                        {
                            FireLine(dataBuf.ToString());
                            dataBuf.Clear();
                        }
                        continue;
                    }

                    // Comment lines (start with ':') and other field
                    // types ('event:', 'id:', 'retry:') are ignored —
                    // we only care about JSON-RPC payloads in `data:`.
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var data = line.Substring(5);
                        // SSE allows one optional space after the colon.
                        if (data.StartsWith(" ", StringComparison.Ordinal))
                            data = data.Substring(1);
                        if (dataBuf.Length > 0) dataBuf.Append('\n');
                        dataBuf.Append(data);
                    }
                }

                // Flush a final event without trailing blank line.
                if (dataBuf.Length > 0) FireLine(dataBuf.ToString());
            }
            catch (OperationCanceledException) { /* shutdown — clean */ }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-http:" + _serverName, "SSE drain error", ex);
                FireFaulted("SSE stream error: " + ex.Message);
            }
            finally
            {
                resp.Dispose();
            }
        }

        private void FireLine(string line)
        {
            try { LineReceived?.Invoke(line); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-http:" + _serverName,
                    "LineReceived handler threw", ex);
            }
        }

        private void FireFaulted(string? reason)
        {
            // Fire at most once. Multiple paths can race here (HTTP error
            // + SSE drain failure + DisposeAsync), so we gate.
            if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
            try { Faulted?.Invoke(reason); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-http:" + _serverName,
                    "Faulted handler threw", ex);
            }
        }

        private static async Task<string> SafeReadAsync(
            HttpResponseMessage resp, CancellationToken ct)
        {
            try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        public async ValueTask DisposeAsync()
        {
            try { _cts?.Cancel(); } catch { }

            // Polite shutdown: the spec says clients SHOULD send DELETE
            // with the session id so the server can reclaim state. Best
            // effort — bounded by SessionTerminateBudget so an
            // unreachable server can't hang dispose.
            if (!string.IsNullOrEmpty(_sessionId))
            {
                try
                {
                    using var del = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
                    del.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId!);
                    if (_headers != null)
                    {
                        foreach (var h in _headers)
                            del.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    using var deadline = new CancellationTokenSource(SessionTerminateBudget);
                    using var resp = await _client.SendAsync(del, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }

            try { _client.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            FireFaulted(null);
        }
    }
}
