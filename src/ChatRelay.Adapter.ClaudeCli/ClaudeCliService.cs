using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Backends;

/// <summary>Runs the `claude` CLI in one-shot stream-json mode and raises events per line.</summary>
public class ClaudeCliService
{
    public event EventHandler<ClaudeEvent>? MessageReceived;
    public event EventHandler<string>? ErrorReceived;

    // Captured from the CLI's `system` init event each turn. On the next call
    // we pass `--resume <id>` so Claude continues the conversation rather than
    // starting fresh — this is how we get chat history continuity without
    // holding a long-running subprocess.
    private string? _sessionId;

    /// <summary>
    /// The CLI session id passed to <c>--resume</c>. Null starts a fresh
    /// conversation on the next prompt. Set by the control when the user
    /// switches chats; read after <see cref="SendPromptAsync"/> finishes to
    /// capture any id the CLI auto-assigned.
    /// </summary>
    public string? SessionId
    {
        get => _sessionId;
        set => _sessionId = value;
    }

    /// <summary>Tool patterns for <c>--allowedTools</c>; null/empty omits the flag.</summary>
    public IReadOnlyList<string>? AllowedTools { get; set; }

    /// <summary>Tool patterns for <c>--disallowedTools</c>; null/empty omits the flag.</summary>
    public IReadOnlyList<string>? DisallowedTools { get; set; }

    /// <summary>
    /// MCP tool id to hand to <c>--permission-prompt-tool</c> — the CLI calls
    /// this whenever a tool-use needs approval instead of emitting a TTY
    /// prompt. Typically <c>mcp__&lt;server&gt;__approve</c>. Null omits the flag.
    /// </summary>
    public string? PermissionPromptTool { get; set; }

    /// <summary>CLI `--model` value (e.g. "opus" / "sonnet" / "haiku"). Null omits the flag.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Path to a merged <c>.chatrelay.mcp.json</c>-shaped config. Passed to
    /// the CLI via <c>--mcp-config</c> when set; null omits the flag.
    /// </summary>
    public string? McpConfigPath { get; set; }

    /// <summary>
    /// Working directory for the spawned <c>claude</c> process. When set to
    /// the solution directory the CLI picks up any project-local context
    /// (including its own .mcp.json auto-discovery) relative to the user's
    /// code. Null inherits from VS.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Extra directories to grant tool access to via <c>--add-dir</c>. One
    /// flag per entry. Null/empty omits the flag. Typically populated from
    /// <c>PermissionSettings.AdditionalDirectories</c>.
    /// </summary>
    public IReadOnlyList<string>? AdditionalDirectories { get; set; }

    public async Task SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        if (prompt is null) throw new ArgumentNullException(nameof(prompt));

        // Every user-influenceable value goes through QuoteArg so a stray
        // double-quote, backslash, or space can't break argument boundaries.
        // Previous code spliced values into a string with bare "%s" wrapping,
        // which meant a path / pattern with a literal " would slice the
        // command line open.
        var sb = new StringBuilder("-p --output-format stream-json --verbose");

        if (!string.IsNullOrEmpty(Model))
            sb.Append(" --model ").Append(QuoteArg(Model));
        if (!string.IsNullOrEmpty(_sessionId))
            sb.Append(" --resume ").Append(QuoteArg(_sessionId));
        if (!string.IsNullOrEmpty(McpConfigPath))
            sb.Append(" --mcp-config ").Append(QuoteArg(McpConfigPath));
        if (!string.IsNullOrEmpty(PermissionPromptTool))
            sb.Append(" --permission-prompt-tool ").Append(QuoteArg(PermissionPromptTool));

        // Allow/deny lists — one quoted comma-separated string each, the
        // format the CLI's tokenizer accepts. Skipped entirely when empty
        // so an untouched config leaves CLI's own defaults in place.
        if (AllowedTools != null && AllowedTools.Count > 0)
            sb.Append(" --allowedTools ").Append(QuoteArg(JoinToolList(AllowedTools)));
        if (DisallowedTools != null && DisallowedTools.Count > 0)
            sb.Append(" --disallowedTools ").Append(QuoteArg(JoinToolList(DisallowedTools)));

        // --add-dir grants tool access to extra directories outside the
        // sandboxed cwd. One flag per directory; each path quoted in case
        // it contains spaces. Blank lines are skipped so an empty textbox
        // doesn't emit an empty --add-dir.
        if (AdditionalDirectories != null)
        {
            foreach (var dir in AdditionalDirectories)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                sb.Append(" --add-dir ").Append(QuoteArg(dir.Trim()));
            }
        }

        var arguments = sb.ToString();

        // Assumes `claude` is on PATH. All three pipes are forced to UTF-8;
        // without this the default is the console code page (usually 1252),
        // which mangles non-ASCII characters coming back from the CLI.
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Note: StandardInputEncoding is .NET Core+; we write raw UTF-8 bytes below.
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(WorkingDirectory))
            psi.WorkingDirectory = WorkingDirectory;

        using var process = new Process { StartInfo = psi };

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                ErrorReceived?.Invoke(this, e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        // Kill the subprocess when the caller cancels. The stdout read loop
        // will then see EOF and exit naturally.
        using var cancelReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { /* already exited or access denied */ }
        });

        // Write the prompt as UTF-8 bytes (the default StreamWriter uses the
        // console code page), then close stdin so Claude knows input is done.
        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        await process.StandardInput.BaseStream.WriteAsync(promptBytes, 0, promptBytes.Length);
        process.StandardInput.Close();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var evt = ParseEvent(line);
                if (evt is not null)
                {
                    // Update the tracked session id before forwarding so the
                    // UI and next turn see a consistent view.
                    if (evt.Type == ClaudeEventType.System) CaptureSessionId(line);
                    MessageReceived?.Invoke(this, evt);
                }
            }
            catch (JsonException ex)
            {
                ErrorReceived?.Invoke(this, $"Parse error: {ex.Message} on line: {line}");
            }
        }

        await Task.Run(() => process.WaitForExit(), ct);

        // Propagate cancellation so the caller can distinguish "user cancelled"
        // from "CLI finished normally".
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

        // If the CLI exited with an error and we had a tracked session, drop
        // it — the session may have expired or been invalidated, and retrying
        // --resume against it would just keep failing.
        if (process.ExitCode != 0) _sessionId = null;
    }

    private void CaptureSessionId(string systemEventJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(systemEventJson);
            if (doc.RootElement.TryGetProperty("session_id", out var s)
                && s.ValueKind == JsonValueKind.String)
            {
                _sessionId = s.GetString();
            }
        }
        catch { /* malformed event — keep previous id */ }
    }

    private static ClaudeEvent? ParseEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl)) return null;

        return typeEl.GetString() switch
        {
            "assistant" => BuildAssistantEvent(root),
            "user" => BuildUserEvent(root),
            "result" => new ClaudeEvent
            {
                Type = ClaudeEventType.Result,
                Content = root.TryGetProperty("result", out var r) ? (r.GetString() ?? "") : "",
                InputTokens = ReadUsageInt(root, "input_tokens"),
                OutputTokens = ReadUsageInt(root, "output_tokens"),
                CacheReadTokens = ReadUsageInt(root, "cache_read_input_tokens"),
                CacheWriteTokens = ReadUsageInt(root, "cache_creation_input_tokens"),
                CostUsd = root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDouble()
                    : (double?)null,
                HasUsage = root.TryGetProperty("usage", out _) || root.TryGetProperty("total_cost_usd", out _)
            },
            "system" => new ClaudeEvent
            {
                Type = ClaudeEventType.System,
                Content = root.GetRawText()
            },
            _ => null
        };
    }

    // CLI emits a "user" event after every tool execution that carries
    // tool_result content blocks correlated to prior tool_use ids. We pull
    // the ids out so the adapter can map back to (ToolName, InputJson) and
    // signal "tool finished, post-write state is on disk" to the change
    // tracker. Other content (text echoes from the user role) is ignored.
    private static ClaudeEvent BuildUserEvent(JsonElement root)
    {
        var evt = new ClaudeEvent { Type = ClaudeEventType.User };
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return evt;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t)) continue;
            if (t.GetString() != "tool_result") continue;
            evt.ToolResults.Add(new ClaudeToolResult
            {
                ToolUseId = block.TryGetProperty("tool_use_id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? string.Empty
                    : string.Empty,
                IsError = block.TryGetProperty("is_error", out var err)
                    && err.ValueKind == JsonValueKind.True,
            });
        }
        return evt;
    }

    // CLI expects --allowedTools / --disallowedTools as a single comma-separated
    // argument. Individual patterns can contain parens + colons
    // (e.g. "Bash(git log:*)"). The joined value is passed through QuoteArg
    // by the caller so embedded double-quotes / backslashes are escaped
    // correctly before hitting the CLI's tokenizer.
    private static string JoinToolList(IReadOnlyList<string> patterns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < patterns.Count; i++)
        {
            var p = patterns[i];
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(p);
        }
        return sb.ToString();
    }

    // Quote a value for inclusion in a Windows CRT-parsed command line.
    // Rules (per the CommandLineToArgvW spec):
    //   • A run of N backslashes followed by a " becomes 2N backslashes + \".
    //   • A run of N backslashes NOT followed by " stays N backslashes.
    //   • A trailing run of backslashes before the closing " is doubled.
    // Values with no whitespace, quotes, or backslashes can be emitted raw.
    // ProcessStartInfo on net48 takes a flat Arguments string, so we cannot
    // rely on ArgumentList (net5+) to do this for us — hence the helper.
    private static string QuoteArg(string? value)
    {
        if (value == null) return "\"\"";
        var needsQuote = value.Length == 0
            || value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
            || value.IndexOf('\\') >= 0;
        if (!needsQuote) return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) sb.Append('\\', backslashes);
                sb.Append(c);
                backslashes = 0;
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    // CLI nests token counts under `usage` but emits `total_cost_usd` at the root.
    // Returns 0 if missing or malformed so we don't fail the parse over a single field.
    // Accepts Int32, Int64, and Double shapes — some backends emit numerics
    // as floats and GetInt32 would throw FormatException on those.
    private static int ReadUsageInt(JsonElement root, string key)
    {
        if (!root.TryGetProperty("usage", out var usage)) return 0;
        if (!usage.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt32(out var i)) return i;
        if (v.TryGetInt64(out var l)) return l > int.MaxValue ? int.MaxValue : (int)l;
        if (v.TryGetDouble(out var d)) return d > int.MaxValue ? int.MaxValue : (int)d;
        return 0;
    }

    // Anthropic assistant message shape:
    //   { "message": { "content": [ { "type": "text" | "thinking" | "redacted_thinking", ... } ] } }
    // Pulls text and thinking blocks into separate buffers so the adapter can
    // surface them as two distinct events. Also logs every block type we
    // see — extended thinking is keyword-triggered on the CLI side, and the
    // log is the easiest way to confirm whether thinking blocks are actually
    // arriving vs. being silently absent.
    private static ClaudeEvent BuildAssistantEvent(JsonElement root)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var typesSeen = new System.Collections.Generic.List<string>();
        var toolUses = new System.Collections.Generic.List<ClaudeToolUse>();

        if (root.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var t)) continue;
                var kind = t.GetString();
                if (kind == null) continue;
                typesSeen.Add(kind);

                if (kind == "text" && block.TryGetProperty("text", out var tx))
                {
                    text.Append(tx.GetString());
                }
                else if (kind == "thinking" && block.TryGetProperty("thinking", out var th))
                {
                    thinking.Append(th.GetString());
                }
                else if (kind == "redacted_thinking")
                {
                    // Content is encrypted/safety-filtered — not human-readable.
                    // Show a placeholder so the user knows reasoning happened,
                    // even though we can't display the text.
                    if (thinking.Length > 0) thinking.Append('\n');
                    thinking.Append("[redacted reasoning — not available]");
                }
                else if (kind == "tool_use")
                {
                    // Surface every tool invocation the model emits. The
                    // adapter caches (id → name + input) and forwards as
                    // ToolCallObservedEvent so the change tracker can
                    // snapshot pre-write content for file-mutating tools.
                    toolUses.Add(new ClaudeToolUse
                    {
                        Id = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? string.Empty
                            : string.Empty,
                        Name = block.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() ?? string.Empty
                            : string.Empty,
                        InputJson = block.TryGetProperty("input", out var inputEl)
                            ? inputEl.GetRawText()
                            : "{}",
                    });
                }
            }
        }

        if (typesSeen.Count > 0)
            ChatRelay.Logging.ExtensionLogger.Debug("claude-cli",
                "assistant blocks: " + string.Join(", ", typesSeen));

        return new ClaudeEvent
        {
            Type = ClaudeEventType.AssistantMessage,
            Content = text.ToString(),
            Thinking = thinking.ToString(),
            ToolUses = toolUses,
        };
    }
}

public class ClaudeEvent
{
    public ClaudeEventType Type { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>Extended-thinking content; populated on AssistantMessage events when the CLI emits thinking blocks.</summary>
    public string Thinking { get; set; } = string.Empty;

    /// <summary>Tool-use blocks observed in an AssistantMessage. Empty when the model only emitted text/thinking.</summary>
    public List<ClaudeToolUse> ToolUses { get; set; } = new();

    /// <summary>Tool-result blocks observed in a User event — one per completed tool execution.</summary>
    public List<ClaudeToolResult> ToolResults { get; set; } = new();

    // Usage accounting; populated only for Result events that carry a `usage` block.
    public bool HasUsage { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public double? CostUsd { get; set; }
}

public class ClaudeToolUse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string InputJson { get; set; } = string.Empty;
}

public class ClaudeToolResult
{
    public string ToolUseId { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

public enum ClaudeEventType
{
    System,
    AssistantMessage,
    User,
    Result
}
