using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Stdio transport for MCP: spawns a child process, JSON-RPC lines flow
    /// over stdin/stdout, stderr is captured for diagnostics. Owns every
    /// process-level concern (PATHEXT resolution, stderr tail, graceful
    /// shutdown grace period before SIGKILL) so <see cref="McpServerHandle"/>
    /// stays focused on protocol-level work.
    /// </summary>
    public sealed class StdioMcpTransport : IMcpTransport
    {
        private readonly string _serverName;
        private readonly string _command;
        private readonly IReadOnlyList<string>? _args;
        private readonly IReadOnlyDictionary<string, string>? _env;
        private readonly string _workingDirectory;

        // Rolling tail of stderr lines from the spawned process. When the
        // process exits abnormally we surface the last line as the fault
        // reason so a crash-on-start tells the user what actually went
        // wrong (missing dependency, bad config) rather than just
        // "exited unexpectedly". Bounded so a chatty server can't eat
        // memory.
        private readonly Queue<string> _stderrTail = new();
        private const int StderrTailCapacity = 8;
        private readonly object _stderrLock = new();

        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _readerCts;
        private Task? _readerTask;
        private int _faulted;

        // Grace period between closing stdin (polite shutdown signal) and
        // SIGKILL'ing the server process. Most MCP servers reap stdin-EOF
        // and exit cleanly within tens of milliseconds; the budget exists
        // for the rare server that ignores stdin closure.
        private static readonly TimeSpan GracefulShutdownGrace = TimeSpan.FromMilliseconds(500);

        public event Action<string>? LineReceived;
        public event Action<string?>? Faulted;

        public StdioMcpTransport(
            string serverName,
            string command,
            IReadOnlyList<string>? args,
            IReadOnlyDictionary<string, string>? env,
            string workingDirectory)
        {
            _serverName = serverName;
            _command = command;
            _args = args;
            _env = env;
            _workingDirectory = workingDirectory;
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            // Resolve the command through PATHEXT so bare names like
            // "mcp-infofetch" pick up the `.cmd` / `.bat` / `.exe`
            // actually on disk — CreateProcess won't do this itself
            // when stdin/stdout are redirected. If the resolved file
            // is a batch wrapper (e.g. a dotnet global-tool shim),
            // route through cmd.exe so it actually executes.
            var (launchFile, launchArgs) = ResolveLaunch(_command, BuildArgs(_args));

            var psi = new ProcessStartInfo
            {
                FileName = launchFile,
                Arguments = launchArgs,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workingDirectory,
            };
            if (_env != null)
            {
                foreach (var kv in _env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            _readerCts = new CancellationTokenSource();
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.ErrorDataReceived += OnStderrLine;

            _process.Start();
            _process.BeginErrorReadLine();

            _stdin = _process.StandardInput;
            _readerTask = Task.Run(() => ReadStdoutLoopAsync(_process.StandardOutput, _readerCts.Token));

            return Task.CompletedTask;
        }

        public async Task SendLineAsync(string jsonLine, CancellationToken ct)
        {
            var stdin = _stdin;
            if (stdin == null)
                throw new InvalidOperationException("StdioMcpTransport.SendLineAsync: stdin is not open.");

            ct.ThrowIfCancellationRequested();
            await stdin.WriteLineAsync(jsonLine).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }

        // Background reader loop fires LineReceived for each non-empty line
        // the server writes to stdout. JSON parsing happens upstream — the
        // transport doesn't care about content, only framing.
        private async Task ReadStdoutLoopAsync(StreamReader reader, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { LineReceived?.Invoke(line); }
                    catch (Exception ex)
                    {
                        ExtensionLogger.Warn("mcp-stdio:" + _serverName,
                            "LineReceived handler threw", ex);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown — clean exit */ }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-stdio:" + _serverName, "Reader loop error", ex);
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            // Process death = transport break. Surface the last stderr
            // line as the reason so the handle's user-facing error has
            // useful content.
            var tail = GetStderrTail();
            var reason = string.IsNullOrEmpty(tail)
                ? "Server process exited unexpectedly (no stderr output)."
                : "Server exited: " + tail;
            FireFaulted(reason);
        }

        private void OnStderrLine(object? sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            ExtensionLogger.Debug("mcp-stdio:" + _serverName, "stderr: " + e.Data);
            lock (_stderrLock)
            {
                _stderrTail.Enqueue(e.Data!);
                while (_stderrTail.Count > StderrTailCapacity) _stderrTail.Dequeue();
            }
        }

        private void FireFaulted(string? reason)
        {
            // Fire at most once. Multiple paths can race here (Exited +
            // ConnectAsync exception + DisposeAsync), so we gate.
            if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
            try { Faulted?.Invoke(reason); }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp-stdio:" + _serverName,
                    "Faulted handler threw", ex);
            }
        }

        private string GetStderrTail()
        {
            lock (_stderrLock)
            {
                if (_stderrTail.Count == 0) return string.Empty;
                return _stderrTail.ToArray()[_stderrTail.Count - 1];
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _readerCts?.Cancel(); } catch { }

            var proc = _process;
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    try { _stdin?.Close(); } catch { }

                    using var grace = new CancellationTokenSource(GracefulShutdownGrace);
                    try
                    {
                        await proc.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(); } catch { }
                        try { await proc.WaitForExitAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }
            catch { /* best effort */ }
            finally
            {
                if (proc != null) proc.Exited -= OnProcessExited;
                try { proc?.Dispose(); } catch { }
                _process = null;
                _stdin = null;
                try { _readerCts?.Dispose(); } catch { }
                _readerCts = null;
            }

            // If we got disposed before Exited fired, still notify so any
            // pending handle-side waiters unblock.
            FireFaulted(null);
        }

        // --- PATHEXT / batch-file launch resolution -------------------------

        // Windows CreateProcess (used when UseShellExecute is false + stdio
        // is redirected) requires a PE executable — it can't launch .cmd /
        // .bat scripts directly. And when the command is a bare name with
        // no extension, CreateProcess doesn't walk PATHEXT the way the
        // shell does. Resolve both gaps here so a user's config of
        // <c>"command": "mcp-infofetch"</c> (a dotnet global-tool .cmd
        // wrapper) launches cleanly.
        //
        // Returns the filename + argument string to hand to ProcessStartInfo.
        // For batch files: filename=cmd.exe, arguments=/c ""path" userArgs"
        // For everything else: resolved path (absolute if we found it), args unchanged.
        internal static (string fileName, string arguments) ResolveLaunch(string command, string argsString)
        {
            if (string.IsNullOrEmpty(command)) return (command, argsString);

            // Already an absolute / relative path that exists? Use as-is
            // unless it's a batch file (which still needs cmd.exe).
            if (File.Exists(command))
            {
                if (IsBatch(command))
                    return ("cmd.exe", $"/c \"\"{command}\" {argsString}\"");
                return (command, argsString);
            }

            // Has an extension already — let CreateProcess deal with PATH.
            if (Path.HasExtension(command))
                return (command, argsString);

            // Bare name — walk PATHEXT manually.
            var resolved = ResolveOnPath(command);
            if (resolved != null)
            {
                if (IsBatch(resolved))
                    return ("cmd.exe", $"/c \"\"{resolved}\" {argsString}\"");
                return (resolved, argsString);
            }

            // Couldn't resolve — let CreateProcess raise the error.
            return (command, argsString);
        }

        private static bool IsBatch(string path)
        {
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveOnPath(string command)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var pathExt = Environment.GetEnvironmentVariable("PATHEXT")
                              ?? ".COM;.EXE;.BAT;.CMD";
                var dirs = pathEnv.Split(Path.PathSeparator,
                                         StringSplitOptions.RemoveEmptyEntries);
                var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var dir in dirs)
                {
                    foreach (var e in extensions)
                    {
                        try
                        {
                            var candidate = Path.Combine(dir, command + e);
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch { /* invalid PATH entry, skip */ }
                    }
                }
            }
            catch { /* env access / Path.Combine failure — give up */ }
            return null;
        }

        private static string BuildArgs(IReadOnlyList<string>? args)
        {
            if (args == null || args.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                var a = args[i] ?? string.Empty;
                if (a.Contains(' ') || a.Contains('"'))
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(a);
            }
            return sb.ToString();
        }
    }
}
