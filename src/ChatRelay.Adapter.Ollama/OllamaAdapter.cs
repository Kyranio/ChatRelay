using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Mcp;
using ChatRelay.Logging;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Adapter over a locally-running Ollama daemon
    /// (<c>http://localhost:11434</c> by default, overridable via
    /// <c>OLLAMA_HOST</c>). Auto-discovered: if <c>/api/tags</c> responds
    /// within <see cref="ProbeBudget"/> we consider it installed and enumerate
    /// the models it reports. Stateless — we send the full chat history on
    /// every call.
    /// </summary>
    public class OllamaAdapter : AiAdapterBase
    {
        public const string AdapterId = "ollama";

        public override string Id => AdapterId;
        public override string DisplayName => "Ollama";

        public override AiCapabilities Capabilities { get; } = new AiCapabilities
        {
            StatefulSessions = false,
            PermissionModes = false,
            Streaming = true
        };

        // One client for everything. We don't set HttpClient.Timeout because it
        // applies process-wide to every request that flows through this client
        // and there's no way to opt out per-call. Per-call deadlines are
        // expressed via linked CancellationTokenSources at the call site, so
        // the caller's CT always wins and the deadline is named, not magic.
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // Probe budget: how long we'll wait for /api/tags to respond before
        // declaring Ollama unavailable. A cold daemon (or a sleepy laptop)
        // can easily take >5s. Anything tighter than this silently drops
        // Ollama from the dropdown for valid setups.
        private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(15);

        private static string BaseUrl()
        {
            var env = Environment.GetEnvironmentVariable("OLLAMA_HOST");
            if (string.IsNullOrEmpty(env)) return "http://localhost:11434";
            // OLLAMA_HOST can be "host:port" or a full URL.
            if (env.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return env.TrimEnd('/');
            return "http://" + env.TrimEnd('/');
        }

        public override async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            // Soft probe. Any failure — timeout, connection refused, firewall,
            // 5xx — just means "not installed / not running" for our purposes.
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linked.CancelAfter(ProbeBudget);
                try
                {
                    using (var resp = await Http.GetAsync(BaseUrl() + "/api/tags", linked.Token)
                                                .ConfigureAwait(false))
                    {
                        var ok = resp.IsSuccessStatusCode;
                        ExtensionLogger.Info(AdapterId,
                            ok ? "Ollama reachable at " + BaseUrl()
                               : $"/api/tags returned {(int)resp.StatusCode}");
                        return ok;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // caller-driven cancel propagates
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Info(AdapterId, "Ollama probe: " + ex.Message);
                    return false;
                }
            }
        }

        public override async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linked.CancelAfter(ProbeBudget);
                try
                {
                    using (var resp = await Http.GetAsync(BaseUrl() + "/api/tags", linked.Token)
                                                .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            ExtensionLogger.Warn(AdapterId,
                                $"List models failed: {(int)resp.StatusCode}");
                            return new List<AiModel>();
                        }

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var list = ParseModels(json);
                        ExtensionLogger.Info(AdapterId, $"Listed {list.Count} Ollama models");
                        return list;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn(AdapterId, "List models error", ex);
                    return new List<AiModel>();
                }
            }
        }

        // Ollama /api/tags shape: { "models": [ { "name": "llama3.2:3b", ... }, ... ] }
        private List<AiModel> ParseModels(string json)
        {
            var result = new List<AiModel>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("models", out var models)) return result;

                foreach (var m in models.EnumerateArray())
                {
                    if (!m.TryGetProperty("name", out var nameEl)) continue;
                    var fullName = nameEl.GetString();
                    if (string.IsNullOrEmpty(fullName)) continue;

                    // "llama3.2:3b" → name "Llama 3.2", version "3b"
                    SplitNameVersion(fullName, out var display, out var version);

                    result.Add(new AiModel
                    {
                        AdapterId = Id,
                        AdapterDisplayName = DisplayName,
                        Id = fullName!,
                        DisplayName = display,
                        Version = version
                    });
                }
            }
            return result;
        }

        // Ollama model ids look like "family[version]:tag". We split on ':'
        // for the tag, then prettify the family-version part for the bold
        // label (e.g. "llama3.2" → "Llama 3.2").
        private static void SplitNameVersion(string? id, out string name, out string version)
        {
            version = "";
            name = id ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                return;
            var colon = id!.IndexOf(':');
            if (colon >= 0)
            {
                version = id.Substring(colon + 1);
                name = id.Substring(0, colon);
            }

            // Capitalise and insert a space between letters and digits so
            // "llama3.2" reads as "Llama 3.2". Non-destructive — if the
            // pattern doesn't match, we leave it alone.
            if (!string.IsNullOrEmpty(name))
            {
                var sb = new StringBuilder();
                sb.Append(char.ToUpperInvariant(name[0]));
                for (int i = 1; i < name.Length; i++)
                {
                    var prev = name[i - 1];
                    var c = name[i];
                    if (char.IsLetter(prev) && char.IsDigit(c)) sb.Append(' ');
                    sb.Append(c);
                }
                name = sb.ToString();
            }
        }

        // Max tool-use iterations per turn. Same reasoning as the API
        // adapter — a model that keeps calling tools forever shouldn't
        // block the send indefinitely.
        private const int MaxToolIterations = 10;

        public override async Task SendPromptAsync(AiRequest request, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(request.Model))
            {
                RaiseError("Ollama requires an explicit model id (pick one from the dropdown).");
                throw new InvalidOperationException("No Ollama model selected");
            }

            // MCP tool schemas — same pattern as ClaudeApiAdapter. Starting
            // servers up-front avoids paying the handshake cost inside the
            // tool-use inner loop. Start failures just mean fewer tools
            // exposed, not a failed send.
            IReadOnlyList<McpToolDescriptor> mcpTools = Array.Empty<McpToolDescriptor>();
            if (request.Mcp != null)
            {
                try { await request.Mcp.EnsureServersStartedAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ExtensionLogger.Warn(AdapterId, "MCP EnsureServersStartedAsync failed", ex); }
                mcpTools = request.Mcp.ListAvailableTools();
            }

            ExtensionLogger.Info(AdapterId,
                $"Send: model={request.Model} historyTurns={request.History?.Count ?? 0} "
                + $"promptLen={request.Prompt?.Length ?? 0} mcpTools={mcpTools.Count}");

            RaiseMessage(new AiMessageEvent
            {
                Kind = AiEventKind.ModelInfo,
                ModelDisplayName = PrettyLabel(request.Model)
            });

            // Mutable message list we extend in subsequent iterations when
            // the model requests tools.
            var messages = BuildInitialMessages(request);

            var totalUsage = new AiUsage();
            bool anyUsage = false;

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();

                var body = BuildRequestBody(request.Model!, messages, mcpTools);

                var assistantText = new StringBuilder();
                var assistantThinking = new StringBuilder();
                var toolCalls = new List<OllamaToolCall>();
                var turnUsage = new AiUsage();
                bool turnHasUsage = false;

                using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl() + "/api/chat"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                                .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            ExtensionLogger.Error(AdapterId,
                                $"Ollama {(int)resp.StatusCode}: {errBody}");
                            RaiseError($"Ollama error {(int)resp.StatusCode}: {errBody}");
                            return;
                        }

                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            await ConsumeNdjsonAsync(
                                reader, assistantText, assistantThinking, toolCalls,
                                turnUsage, h => turnHasUsage = h, ct).ConfigureAwait(false);
                        }
                    }
                }

                if (turnHasUsage)
                {
                    totalUsage.InputTokens += turnUsage.InputTokens;
                    totalUsage.OutputTokens += turnUsage.OutputTokens;
                    anyUsage = true;
                }

                // Emit whatever visible content the model produced this
                // turn — same <think>…</think> extraction as before so
                // legacy reasoning models still get a thinking bubble.
                EmitThinkingAndAssistant(assistantThinking, assistantText);

                // No tool calls (or no runtime to dispatch to) → turn done.
                if (toolCalls.Count == 0 || request.Mcp == null) break;

                // Record the assistant turn in history exactly as Ollama
                // will parse it on the next request.
                messages.Add(new OllamaMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = toolCalls
                });

                // Execute each tool call; the resulting `tool` messages
                // become the user-side response the model sees next turn.
                foreach (var call in toolCalls)
                {
                    var parsed = request.Mcp.TryParseToolId(call.Name);
                    string resultText;
                    if (parsed == null)
                    {
                        resultText = $"Unknown tool: {call.Name}";
                    }
                    else
                    {
                        JsonElement args = default;
                        if (!string.IsNullOrEmpty(call.ArgumentsJson))
                        {
                            try
                            {
                                using var d = JsonDocument.Parse(call.ArgumentsJson);
                                args = d.RootElement.Clone();
                            }
                            catch (JsonException) { /* empty args */ }
                        }
                        var r = await request.Mcp.CallToolAsync(
                            parsed.Value.Server, parsed.Value.Tool, args, ct).ConfigureAwait(false);
                        resultText = r.Content;
                    }

                    messages.Add(new OllamaMessage
                    {
                        Role = "tool",
                        Content = resultText,
                        ToolName = call.Name
                    });
                }
            }

            if (anyUsage)
            {
                RaiseMessage(new AiMessageEvent
                {
                    Kind = AiEventKind.UsageUpdate,
                    Usage = totalUsage
                });
            }
        }

        // Seed the message list with prior turns + the current user prompt.
        private static List<OllamaMessage> BuildInitialMessages(AiRequest request)
        {
            var result = new List<OllamaMessage>();
            if (request.History != null)
            {
                foreach (var t in request.History)
                {
                    result.Add(new OllamaMessage
                    {
                        Role = t.Role == AiTurnRole.Assistant ? "assistant" : "user",
                        Content = t.Content ?? string.Empty
                    });
                }
            }
            result.Add(new OllamaMessage { Role = "user", Content = request.Prompt ?? string.Empty });
            return result;
        }

        private static string BuildRequestBody(string model, List<OllamaMessage> messages, IReadOnlyList<McpToolDescriptor> mcpTools)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", model);
                    w.WriteBoolean("stream", true);

                    // Tools block. Ollama follows the OpenAI-style "function"
                    // shape: { type: "function", function: { name, description, parameters } }.
                    // `parameters` is the JSON schema object — same content
                    // we forward to Claude API under the `input_schema` name.
                    if (mcpTools.Count > 0)
                    {
                        w.WriteStartArray("tools");
                        foreach (var tool in mcpTools)
                        {
                            w.WriteStartObject();
                            w.WriteString("type", "function");
                            w.WritePropertyName("function");
                            w.WriteStartObject();
                            w.WriteString("name", tool.QualifiedId);
                            if (!string.IsNullOrEmpty(tool.Description))
                                w.WriteString("description", tool.Description);
                            w.WritePropertyName("parameters");
                            if (tool.InputSchema.ValueKind == JsonValueKind.Object)
                                tool.InputSchema.WriteTo(w);
                            else
                            {
                                w.WriteStartObject();
                                w.WriteString("type", "object");
                                w.WriteEndObject();
                            }
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    w.WriteStartArray("messages");
                    foreach (var msg in messages) WriteMessage(w, msg);
                    w.WriteEndArray();

                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void WriteMessage(Utf8JsonWriter w, OllamaMessage msg)
        {
            w.WriteStartObject();
            w.WriteString("role", msg.Role);
            w.WriteString("content", msg.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(msg.ToolName))
            {
                // `tool` role messages carry the tool name so the model
                // knows which call this result corresponds to.
                w.WriteString("tool_name", msg.ToolName);
            }
            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var call in msg.ToolCalls)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", call.Name ?? string.Empty);
                    w.WritePropertyName("arguments");
                    if (!string.IsNullOrEmpty(call.ArgumentsJson))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(call.ArgumentsJson);
                            d.RootElement.WriteTo(w);
                        }
                        catch (JsonException)
                        {
                            w.WriteStartObject(); w.WriteEndObject();
                        }
                    }
                    else
                    {
                        w.WriteStartObject(); w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        // Internal representation of one chat message for the tool-use
        // loop. Only the fields relevant to Role are populated when
        // serialized by WriteMessage.
        private sealed class OllamaMessage
        {
            public string Role { get; set; } = "user";
            public string Content { get; set; } = string.Empty;
            public string? ToolName { get; set; }                       // role=tool
            public List<OllamaToolCall>? ToolCalls { get; set; }        // role=assistant
        }

        private sealed class OllamaToolCall
        {
            public string Name { get; set; } = string.Empty;
            // Serialized JSON object; empty string means "{}".
            public string ArgumentsJson { get; set; } = string.Empty;
        }

        // Matches <think>...</think> blocks emitted by reasoning models that
        // don't expose a separate thinking field (DeepSeek-R1, QwQ, etc.).
        private static readonly Regex ThinkTagRegex =
            new Regex(@"<think>([\s\S]*?)</think>", RegexOptions.Compiled);

        // Ollama emits newline-delimited JSON, each line
        // { "message": { "role": "assistant", "content": "...", "thinking": "..."?, "tool_calls": [...]? }, "done": false }
        // with a final { "done": true, "prompt_eval_count": N, "eval_count": N, ... }.
        // We collect content + thinking + tool_calls into caller-provided
        // buffers so the outer tool-use loop can decide whether to iterate.
        // Usage arrives on the final chunk; written into <paramref name="usage"/>.
        private async Task ConsumeNdjsonAsync(
            StreamReader reader,
            StringBuilder assistant,
            StringBuilder thinking,
            List<OllamaToolCall> toolCalls,
            AiUsage usage,
            Action<bool> setHasUsage,
            CancellationToken ct)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using (var doc = JsonDocument.Parse(line))
                    {
                        var root = doc.RootElement;

                        if (root.TryGetProperty("message", out var m))
                        {
                            if (m.TryGetProperty("content", out var c)
                                && c.ValueKind == JsonValueKind.String)
                            {
                                assistant.Append(c.GetString());
                            }
                            if (m.TryGetProperty("thinking", out var th)
                                && th.ValueKind == JsonValueKind.String)
                            {
                                thinking.Append(th.GetString());
                            }
                            // Ollama emits the full tool_calls array on
                            // whichever chunk the model produces them
                            // (typically the last one before done:true).
                            // Overwrite rather than append so duplicate
                            // chunks don't multiply the calls.
                            if (m.TryGetProperty("tool_calls", out var tc)
                                && tc.ValueKind == JsonValueKind.Array
                                && tc.GetArrayLength() > 0)
                            {
                                toolCalls.Clear();
                                foreach (var entry in tc.EnumerateArray())
                                {
                                    if (!entry.TryGetProperty("function", out var fn)
                                        || fn.ValueKind != JsonValueKind.Object) continue;
                                    var name = fn.TryGetProperty("name", out var nEl)
                                        && nEl.ValueKind == JsonValueKind.String
                                            ? nEl.GetString() ?? string.Empty
                                            : string.Empty;
                                    var argsJson = "{}";
                                    if (fn.TryGetProperty("arguments", out var argsEl))
                                    {
                                        // arguments may be a JSON object OR a
                                        // JSON-encoded string depending on the
                                        // model/version. Normalise both to
                                        // raw object JSON.
                                        if (argsEl.ValueKind == JsonValueKind.Object)
                                        {
                                            argsJson = argsEl.GetRawText();
                                        }
                                        else if (argsEl.ValueKind == JsonValueKind.String)
                                        {
                                            argsJson = argsEl.GetString() ?? "{}";
                                        }
                                    }
                                    toolCalls.Add(new OllamaToolCall
                                    {
                                        Name = name,
                                        ArgumentsJson = argsJson
                                    });
                                }
                            }
                        }

                        if (root.TryGetProperty("done", out var done)
                            && done.ValueKind == JsonValueKind.True)
                        {
                            var input = ReadInt(root, "prompt_eval_count");
                            var output = ReadInt(root, "eval_count");
                            if (input > 0 || output > 0)
                            {
                                usage.InputTokens += input;
                                usage.OutputTokens += output;
                                setHasUsage(true);
                            }
                            break;
                        }

                        if (root.TryGetProperty("error", out var err))
                        {
                            RaiseError("Ollama: " + err.GetString());
                        }
                    }
                }
                catch (JsonException ex)
                {
                    ExtensionLogger.Warn(AdapterId, "NDJSON parse error: " + line, ex);
                }
            }
        }

        // Splits out any reasoning content (either the separate thinking
        // field or <think>…</think> embedded in content), emits a
        // ThinkingMessage if there's any, and an AssistantMessage for the
        // cleaned text. Leaves the input buffers empty on return.
        private void EmitThinkingAndAssistant(StringBuilder thinking, StringBuilder assistant)
        {
            var text = assistant.ToString();
            var think = thinking.ToString();

            // Legacy reasoning models (DeepSeek-R1, QwQ) embed <think> tags
            // directly in the content stream. Extract them only if the
            // separate thinking field wasn't used.
            if (think.Length == 0 && !string.IsNullOrEmpty(text))
            {
                var matches = ThinkTagRegex.Matches(text);
                if (matches.Count > 0)
                {
                    var thinkBuf = new StringBuilder();
                    foreach (Match match in matches) thinkBuf.Append(match.Groups[1].Value);
                    think = thinkBuf.ToString();
                    text = ThinkTagRegex.Replace(text, "").Trim();
                }
            }

            if (!string.IsNullOrEmpty(think))
            {
                RaiseMessage(new AiMessageEvent
                {
                    Kind = AiEventKind.ThinkingMessage,
                    Content = think
                });
            }
            if (!string.IsNullOrEmpty(text))
            {
                RaiseMessage(new AiMessageEvent
                {
                    Kind = AiEventKind.AssistantMessage,
                    Content = text
                });
            }

            assistant.Clear();
            thinking.Clear();
        }

        private static string PrettyLabel(string? id)
        {
            SplitNameVersion(id, out var name, out var version);
            return string.IsNullOrEmpty(version) ? name : (name + " " + version);
        }

        // Accepts Int32 / Int64 / Double shapes. Some Ollama builds return
        // token counts as floats (`1234.0`); GetInt32 would throw on those
        // and we'd lose the whole usage event for the turn.
        private static int ReadInt(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var v)) return 0;
            if (v.ValueKind != JsonValueKind.Number) return 0;
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetInt64(out var l)) return l > int.MaxValue ? int.MaxValue : (int)l;
            if (v.TryGetDouble(out var d)) return d > int.MaxValue ? int.MaxValue : (int)d;
            return 0;
        }
    }
}
