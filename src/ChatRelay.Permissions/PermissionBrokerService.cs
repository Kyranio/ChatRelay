using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Permissions
{
    /// <summary>
    /// Hosts a named-pipe server the broker exe connects to when Claude CLI
    /// needs permission for a tool use. Each incoming connection gets its
    /// own handler thread; requests are surfaced via the
    /// <see cref="RequestReceived"/> async callback, and the decision the
    /// callback returns is written straight back to the broker over the
    /// same pipe.
    ///
    /// Pipe name is parameterised with the VS process id so multiple VS
    /// instances don't collide. The same name is injected into the
    /// broker's environment via the MCP config so it knows which pipe to
    /// connect to.
    /// </summary>
    public class PermissionBrokerService : IDisposable
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public string PipeName { get; }

        /// <summary>
        /// Called for every incoming permission request. The handler must
        /// eventually return a decision — the broker is blocked on the
        /// pipe read until it does. Returns <see cref="PermissionDecision.Deny"/>
        /// by default if the handler throws.
        /// </summary>
        public Func<PermissionRequest, Task<PermissionDecision>>? RequestReceived { get; set; }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _listener;
        private bool _disposed;

        // Every accepted connection gets a handler task; track them so
        // Dispose can wait them out and we don't leak a mid-flight permission
        // prompt when the tool window closes.
        private readonly ConcurrentBag<Task> _handlers = new ConcurrentBag<Task>();

        public PermissionBrokerService()
        {
            // Process-id scoping keeps two VS instances from fighting over
            // one pipe name. Sharing isn't useful here — each instance has
            // its own tool window / dialogs.
            PipeName = "ChatRelay.Permissions." +
                System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
        }

        public void Start()
        {
            if (_listener != null) return;
            _listener = Task.Run(() => ListenLoopAsync(_cts.Token));
            ExtensionLogger.Info("broker", "Pipe server listening on " + PipeName);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    // One server instance per accept. Reusing across
                    // connections isn't supported by NamedPipeServerStream
                    // the way Unix sockets allow, so we recreate.
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var accepted = server;
                    server = null; // handoff — the handler owns disposal now
                    var handler = Task.Run(() => HandleConnectionAsync(accepted, ct), ct);
                    _handlers.Add(handler);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down — clean exit.
                    server?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("broker", "Accept loop error", ex);
                    server?.Dispose();
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, Utf8NoBom, false, 4096, leaveOpen: true))
                using (var writer = new StreamWriter(pipe, Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true })
                {
                    // Broker sends one line per request, reads one line reply.
                    // Loop in case it ever gets chatty, but typically it's 1:1.
                    string? line;
                    while (!ct.IsCancellationRequested
                           && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        var request = ParseRequest(line);
                        var decision = await InvokeHandlerAsync(request).ConfigureAwait(false);
                        var reply = SerializeDecision(decision);
                        await writer.WriteLineAsync(reply).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("broker", "Client handler error", ex);
            }
        }

        private async Task<PermissionDecision> InvokeHandlerAsync(PermissionRequest request)
        {
            var handler = RequestReceived;
            if (handler == null)
            {
                ExtensionLogger.Warn("broker", "No handler registered — denying by default");
                return new PermissionDecision { Allow = false, Message = "No extension handler registered." };
            }
            try
            {
                return await handler(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("broker", "Handler threw", ex);
                return new PermissionDecision { Allow = false, Message = "Extension error." };
            }
        }

        private static PermissionRequest ParseRequest(string line)
        {
            var req = new PermissionRequest { ToolName = "(unknown)", InputJson = "{}" };
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var r = doc.RootElement;
                    if (r.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String)
                        req.ToolName = tn.GetString() ?? "(unknown)";
                    if (r.TryGetProperty("toolUseId", out var tuid) && tuid.ValueKind == JsonValueKind.String)
                        req.ToolUseId = tuid.GetString() ?? string.Empty;
                    if (r.TryGetProperty("input", out var input))
                        req.InputJson = input.GetRawText();
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("broker", "Request parse error: " + line, ex);
            }
            return req;
        }

        private static string SerializeDecision(PermissionDecision d)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("decision", d.Allow ? "allow" : "deny");
                    if (!d.Allow && !string.IsNullOrEmpty(d.Message))
                        w.WriteString("message", d.Message);
                    w.WriteEndObject();
                }
                return Utf8NoBom.GetString(ms.ToArray());
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Signal cancellation synchronously so callers that Dispose just
            // before a new connection arrives see the shutdown flag. Actual
            // awaiting of the listen loop / handler tasks is fire-and-forget
            // so we don't block the UI thread — a handler blocked on
            // SwitchToMainThreadAsync while the UI thread is waiting here
            // would deadlock.
            try { _cts.Cancel(); } catch { }

            // Awaited tasks below were started elsewhere (Start + accept
            // loop). VSTHRD003 flags that as a potential deadlock risk,
            // but it only applies to JoinableTaskFactory-managed work —
            // this service is purely background, no UI thread involvement,
            // so the warning is a false positive here.
#pragma warning disable VSTHRD003
            _ = Task.Run(async () =>
            {
                try { if (_listener != null) await _listener.ConfigureAwait(false); }
                catch { /* cancellation / drop — expected */ }

                try
                {
                    var snapshot = _handlers.ToArray();
                    if (snapshot.Length > 0)
                        await Task.WhenAll(snapshot).ConfigureAwait(false);
                }
                catch { /* cancellation / drop — expected */ }

                try { _cts.Dispose(); } catch { }
                ExtensionLogger.Info("broker", "Pipe server stopped");
            });
#pragma warning restore VSTHRD003
        }
    }

    /// <summary>One permission request unpacked from the broker.</summary>
    public class PermissionRequest
    {
        public string ToolName { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        /// <summary>Raw JSON of the tool input block — displayed verbatim in the dialog.</summary>
        public string InputJson { get; set; } = "{}";
    }

    /// <summary>The dialog's verdict. <see cref="AlwaysAllow"/> triggers persistence into settings.</summary>
    public class PermissionDecision
    {
        public bool Allow { get; set; }
        public bool AlwaysAllow { get; set; }
        public string? Message { get; set; }
    }
}
