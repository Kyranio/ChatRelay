using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Mcp;
using ChatRelay.Logging;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Adapter over the Anthropic Messages API. Stateless: we send the full
    /// conversation history on every turn. Availability is gated on
    /// <c>ANTHROPIC_API_KEY</c> being set; if present we also hit GET /v1/models
    /// to enumerate exactly what the account can access (so deprecated models
    /// drop off automatically).
    /// </summary>
    public class ClaudeApiAdapter : AiAdapterBase
    {
        public const string AdapterId = "claude-api";

        private const string BaseUrl = "https://api.anthropic.com";
        private const string ApiVersion = "2023-06-01";

        public override string Id => AdapterId;
        public override string DisplayName => "Claude API";

        public override AiCapabilities Capabilities { get; } = new AiCapabilities
        {
            StatefulSessions = false,
            PermissionModes = false,
            Streaming = true
        };

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // On net48 the default negotiates whatever TLS version the OS
            // prefers; Anthropic requires 1.2+. Pin it on the handler instead
            // of touching ServicePointManager — the latter is process-wide
            // and could stomp on whatever the VS host or another extension
            // has configured.
            var handler = new HttpClientHandler();
            try
            {
                // SslProtocols enum value for Tls13 is 12288; expressed as a
                // cast so net48 with an older SDK still compiles.
                handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                       | (System.Security.Authentication.SslProtocols)12288;
            }
            catch
            {
                // Older OS builds may reject Tls13 — fall back to Tls12 alone.
                try { handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12; }
                catch { /* stick with OS default */ }
            }
            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static string? ReadApiKey()
            => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        public override Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            var key = ReadApiKey();
            if (string.IsNullOrEmpty(key))
            {
                ExtensionLogger.Info(AdapterId, "No ANTHROPIC_API_KEY in environment");
                return Task.FromResult(false);
            }

            // Don't validate the key on startup — a bad key would just fail
            // loudly on the first send, which is fine. Keeps probe cheap and
            // avoids a network round-trip when VS starts.
            ExtensionLogger.Info(AdapterId, "ANTHROPIC_API_KEY detected");
            return Task.FromResult(true);
        }

        public override async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
        {
            var key = ReadApiKey();
            if (string.IsNullOrEmpty(key)) return new List<AiModel>();

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1/models"))
                {
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", ApiVersion);

                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            ExtensionLogger.Warn(AdapterId,
                                $"List models failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                            return new List<AiModel>();
                        }

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var list = ParseModels(json);
                        ExtensionLogger.Info(AdapterId, $"Listed {list.Count} models");
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn(AdapterId, "List models error", ex);
                return new List<AiModel>();
            }
        }

        private List<AiModel> ParseModels(string json)
        {
            var result = new List<AiModel>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("data", out var data)) return result;

                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    var display = m.TryGetProperty("display_name", out var dn) ? dn.GetString() : id;
                    // display_name is usually "Claude Opus 4.5"; split off the
                    // version at the trailing digits so we can bold the family.
                    SplitNameVersion(display, out var name, out var version);

                    result.Add(new AiModel
                    {
                        AdapterId = Id,
                        AdapterDisplayName = DisplayName,
                        Id = id!,
                        DisplayName = name,
                        Version = version
                    });
                }
            }
            return result;
        }

        private static void SplitNameVersion(string? display, out string name, out string version)
        {
            name = display ?? "";
            version = "";
            if (string.IsNullOrEmpty(name)) return;

            // Find the last token that starts with a digit — that's the version.
            var lastSpace = name.LastIndexOf(' ');
            if (lastSpace > 0 && lastSpace < name.Length - 1 && char.IsDigit(name[lastSpace + 1]))
            {
                version = name.Substring(lastSpace + 1);
                name = name.Substring(0, lastSpace);
            }
        }

        // Hard ceiling on tool-use rounds per SendPromptAsync call. 10 is a
        // generous upper bound that lets the model chain a few reads +
        // edits without letting a broken server loop forever.
        private const int MaxToolIterations = 10;

        public override async Task SendPromptAsync(AiRequest request, CancellationToken ct)
        {
            var key = ReadApiKey();
            if (string.IsNullOrEmpty(key))
            {
                RaiseError("ANTHROPIC_API_KEY environment variable is not set.");
                throw new InvalidOperationException("Missing ANTHROPIC_API_KEY");
            }

            var model = string.IsNullOrEmpty(request.Model)
                ? "claude-sonnet-4-5-20250929" // safe fallback
                : request.Model;

            // Fetch MCP tool schemas if the caller provided a runtime. We
            // start servers here so the first tool call inside the loop
            // doesn't eat the handshake latency. A failure to start any
            // individual server doesn't abort the send — the model just
            // sees fewer tools.
            IReadOnlyList<McpToolDescriptor> mcpTools = Array.Empty<McpToolDescriptor>();
            if (request.Mcp != null)
            {
                try { await request.Mcp.EnsureServersStartedAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ExtensionLogger.Warn(AdapterId, "MCP EnsureServersStartedAsync failed", ex); }
                mcpTools = request.Mcp.ListAvailableTools();
            }

            ExtensionLogger.Info(AdapterId,
                $"Send: model={model} historyTurns={request.History?.Count ?? 0} "
                + $"promptLen={request.Prompt?.Length ?? 0} mcpTools={mcpTools.Count}");

            // Build the initial messages list from prior history + the
            // current user turn. Subsequent loop iterations append the
            // assistant's tool_use turn and a user tool_result turn.
            var messages = BuildInitialMessages(request);

            // Usage accumulated across every iteration — emit one final
            // UsageUpdate at the end of the whole turn so the UI's per-
            // bubble footer reflects the full cost (tool hops included).
            var totalUsage = new AiUsage();
            bool anyUsage = false;

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();

                var body = BuildRequestBody(model!, messages, mcpTools);

                var assistantBlocks = new List<ApiBlock>();
                string? stopReason = null;
                var turnUsage = new AiUsage();
                bool turnHasUsage = false;

                using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/messages"))
                {
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", ApiVersion);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                                  .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            ExtensionLogger.Error(AdapterId,
                                $"API {(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody}");
                            RaiseError($"Claude API error {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{Truncate(errBody, 500)}");
                            return;
                        }

                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            await ConsumeSseAsync(
                                reader, assistantBlocks, turnUsage,
                                h => turnHasUsage = h,
                                r => stopReason = r, ct).ConfigureAwait(false);
                        }
                    }
                }

                if (turnHasUsage)
                {
                    totalUsage.InputTokens += turnUsage.InputTokens;
                    totalUsage.OutputTokens += turnUsage.OutputTokens;
                    totalUsage.CacheReadTokens += turnUsage.CacheReadTokens;
                    totalUsage.CacheWriteTokens += turnUsage.CacheWriteTokens;
                    anyUsage = true;
                }

                // Surface any text / thinking the model produced this turn.
                // Tool_use blocks aren't shown — the user sees the tool
                // results indirectly through the model's follow-up text.
                foreach (var block in assistantBlocks)
                {
                    if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                        RaiseMessage(new AiMessageEvent { Kind = AiEventKind.AssistantMessage, Content = block.Text });
                    else if (block.Type == "thinking" && !string.IsNullOrEmpty(block.Text))
                        RaiseMessage(new AiMessageEvent { Kind = AiEventKind.ThinkingMessage, Content = block.Text });
                }

                // If the model didn't stop for tool use, the turn is done.
                // Same if we don't actually have a runtime to dispatch to.
                var toolUses = assistantBlocks.Where(b => b.Type == "tool_use").ToList();
                if (stopReason != "tool_use" || request.Mcp == null || toolUses.Count == 0)
                    break;

                // Record the assistant's full content (text + tool_use
                // blocks) in the message history so the follow-up turn
                // has the context the API requires.
                messages.Add(new ApiMessage { Role = "assistant", Blocks = assistantBlocks });

                // Execute each tool. Errors fold into tool_result blocks
                // with isError=true so the model can react instead of us
                // aborting the turn.
                var toolResults = new List<ApiBlock>();
                foreach (var use in toolUses)
                {
                    // Fire the "Requested" observation BEFORE executing so
                    // the change tracker can snapshot the pre-write file
                    // state. Tools that don't mutate files are filtered
                    // out by the tracker; we always emit so future
                    // consumers (tool log) can see everything.
                    RaiseToolCall(use.Name, use.InputJson, ToolCallPhase.Requested);

                    var parsed = request.Mcp.TryParseToolId(use.Name);
                    if (parsed == null)
                    {
                        toolResults.Add(new ApiBlock
                        {
                            Type = "tool_result",
                            ToolUseId = use.Id,
                            Text = $"Unknown tool: {use.Name}",
                            IsError = true
                        });
                        // No execution happened — no point firing Completed
                        // either; the tracker would just re-read identical
                        // content as LastApplied.
                        continue;
                    }

                    JsonElement args = default;
                    if (!string.IsNullOrEmpty(use.InputJson))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(use.InputJson);
                            args = d.RootElement.Clone();
                        }
                        catch (JsonException) { /* empty args — pass default */ }
                    }

                    var result = await request.Mcp.CallToolAsync(
                        parsed.Value.Server, parsed.Value.Tool, args, ct).ConfigureAwait(false);
                    toolResults.Add(new ApiBlock
                    {
                        Type = "tool_result",
                        ToolUseId = use.Id,
                        Text = result.Content,
                        IsError = result.IsError
                    });

                    // Tool finished — post-write state is on disk now.
                    RaiseToolCall(use.Name, use.InputJson, ToolCallPhase.Completed);
                }

                messages.Add(new ApiMessage { Role = "user", Blocks = toolResults });
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

        // Flatten prior turns into our internal message-block form. Each
        // AiTurn in history becomes one message with a single text block;
        // consecutive same-role turns are collapsed because the API
        // rejects them.
        private static List<ApiMessage> BuildInitialMessages(AiRequest request)
        {
            var result = new List<ApiMessage>();

            if (request.History != null)
            {
                foreach (var t in request.History)
                {
                    var role = t.Role == AiTurnRole.Assistant ? "assistant" : "user";
                    var text = t.Content ?? "";
                    if (result.Count > 0 && result[result.Count - 1].Role == role
                        && result[result.Count - 1].Blocks.Count > 0
                        && result[result.Count - 1].Blocks[0].Type == "text")
                    {
                        result[result.Count - 1].Blocks[0].Text += "\n\n" + text;
                    }
                    else
                    {
                        result.Add(new ApiMessage
                        {
                            Role = role,
                            Blocks = new List<ApiBlock>
                            {
                                new ApiBlock { Type = "text", Text = text }
                            }
                        });
                    }
                }
            }

            // Current user prompt merged into the trailing user message
            // when appropriate.
            if (result.Count > 0 && result[result.Count - 1].Role == "user"
                && result[result.Count - 1].Blocks.Count > 0
                && result[result.Count - 1].Blocks[0].Type == "text")
            {
                result[result.Count - 1].Blocks[0].Text += "\n\n" + (request.Prompt ?? "");
            }
            else
            {
                result.Add(new ApiMessage
                {
                    Role = "user",
                    Blocks = new List<ApiBlock>
                    {
                        new ApiBlock { Type = "text", Text = request.Prompt ?? "" }
                    }
                });
            }
            return result;
        }

        // Builds the Anthropic Messages request JSON. Each message's
        // content is serialised as an array of blocks — that's the only
        // form that can carry tool_use / tool_result entries.
        private static string BuildRequestBody(string model, List<ApiMessage> messages, IReadOnlyList<McpToolDescriptor> mcpTools)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", model);
                    // Default output budget. 4096 was the 2024-era cap and
                    // silently truncated long Sonnet / Opus responses
                    // (refactors, reviews). 8192 keeps typical replies
                    // unbounded without inviting runaway output.
                    w.WriteNumber("max_tokens", 8192);
                    w.WriteBoolean("stream", true);

                    // Tool schemas — one entry per exposed MCP tool,
                    // qualified-id as the tool name so we can round-trip
                    // through TryParseToolId when the model calls one.
                    if (mcpTools.Count > 0)
                    {
                        w.WriteStartArray("tools");
                        foreach (var tool in mcpTools)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", tool.QualifiedId);
                            if (!string.IsNullOrEmpty(tool.Description))
                                w.WriteString("description", tool.Description);
                            w.WritePropertyName("input_schema");
                            if (tool.InputSchema.ValueKind == JsonValueKind.Object)
                            {
                                tool.InputSchema.WriteTo(w);
                            }
                            else
                            {
                                // Minimal schema — empty object type. Claude
                                // requires SOMETHING here; an empty object
                                // means "any args are fine".
                                w.WriteStartObject();
                                w.WriteString("type", "object");
                                w.WriteEndObject();
                            }
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    w.WriteStartArray("messages");
                    foreach (var msg in messages)
                    {
                        w.WriteStartObject();
                        w.WriteString("role", msg.Role);
                        w.WritePropertyName("content");
                        w.WriteStartArray();
                        foreach (var block in msg.Blocks) WriteBlock(w, block);
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void WriteBlock(Utf8JsonWriter w, ApiBlock block)
        {
            w.WriteStartObject();
            w.WriteString("type", block.Type);
            switch (block.Type)
            {
                case "text":
                    w.WriteString("text", block.Text ?? string.Empty);
                    break;
                case "thinking":
                    w.WriteString("thinking", block.Text ?? string.Empty);
                    break;
                case "tool_use":
                    w.WriteString("id", block.Id ?? string.Empty);
                    w.WriteString("name", block.Name ?? string.Empty);
                    w.WritePropertyName("input");
                    if (!string.IsNullOrEmpty(block.InputJson))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(block.InputJson);
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
                    break;
                case "tool_result":
                    w.WriteString("tool_use_id", block.ToolUseId ?? string.Empty);
                    if (block.IsError) w.WriteBoolean("is_error", true);
                    w.WriteString("content", block.Text ?? string.Empty);
                    break;
            }
            w.WriteEndObject();
        }

        // SSE parser. Populates <paramref name="blocks"/> with every
        // content block the turn produces (text / thinking / tool_use),
        // records usage as it arrives, and reports the message-level
        // stop_reason to the caller so the outer loop knows whether to
        // iterate again.
        private async Task ConsumeSseAsync(
            StreamReader reader,
            List<ApiBlock> blocks,
            AiUsage usage,
            Action<bool> setHasUsage,
            Action<string?> setStopReason,
            CancellationToken ct)
        {
            // Current block being assembled as deltas arrive. Kind/Text/etc.
            // are rotated on content_block_start and finalised on
            // content_block_stop.
            var current = new ApiBlock { Type = "text" };
            var textBuf = new StringBuilder();
            var inputBuf = new StringBuilder();

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal)) continue; // we route on data.type
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                try
                {
                    using (var doc = JsonDocument.Parse(data))
                    {
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var typeEl)) continue;

                        switch (typeEl.GetString())
                        {
                            case "message_start":
                                if (root.TryGetProperty("message", out var m))
                                {
                                    if (m.TryGetProperty("model", out var modelEl))
                                    {
                                        var display = ModelNameFormatter.FormatModelId(modelEl.GetString());
                                        RaiseMessage(new AiMessageEvent
                                        {
                                            Kind = AiEventKind.ModelInfo,
                                            ModelDisplayName = display
                                        });
                                    }
                                    if (m.TryGetProperty("usage", out var u0))
                                    {
                                        usage.InputTokens += ReadInt(u0, "input_tokens");
                                        usage.CacheReadTokens += ReadInt(u0, "cache_read_input_tokens");
                                        usage.CacheWriteTokens += ReadInt(u0, "cache_creation_input_tokens");
                                        setHasUsage(true);
                                    }
                                }
                                break;

                            case "content_block_start":
                                textBuf.Clear();
                                inputBuf.Clear();
                                current = new ApiBlock { Type = "text" };
                                if (root.TryGetProperty("content_block", out var cb)
                                    && cb.TryGetProperty("type", out var cbt))
                                {
                                    var cbType = cbt.GetString() ?? "text";
                                    current.Type = cbType;
                                    if (cbType == "tool_use")
                                    {
                                        if (cb.TryGetProperty("id", out var idEl))
                                            current.Id = idEl.GetString() ?? "";
                                        if (cb.TryGetProperty("name", out var nEl))
                                            current.Name = nEl.GetString() ?? "";
                                    }
                                }
                                break;

                            case "content_block_delta":
                                if (root.TryGetProperty("delta", out var delta)
                                    && delta.TryGetProperty("type", out var dtype))
                                {
                                    var deltaType = dtype.GetString();
                                    if (deltaType == "text_delta"
                                        && delta.TryGetProperty("text", out var t))
                                    {
                                        textBuf.Append(t.GetString());
                                    }
                                    else if (deltaType == "thinking_delta"
                                        && delta.TryGetProperty("thinking", out var th))
                                    {
                                        textBuf.Append(th.GetString());
                                    }
                                    else if (deltaType == "input_json_delta"
                                        && delta.TryGetProperty("partial_json", out var pj))
                                    {
                                        // tool_use arguments arrive as
                                        // partial JSON fragments — concatenate
                                        // and parse on block_stop.
                                        inputBuf.Append(pj.GetString());
                                    }
                                }
                                break;

                            case "content_block_stop":
                                if (current.Type == "tool_use")
                                {
                                    current.InputJson = inputBuf.Length > 0
                                        ? inputBuf.ToString()
                                        : "{}";
                                    blocks.Add(current);
                                }
                                else if (textBuf.Length > 0)
                                {
                                    current.Text = textBuf.ToString();
                                    blocks.Add(current);
                                }
                                textBuf.Clear();
                                inputBuf.Clear();
                                break;

                            case "message_delta":
                                if (root.TryGetProperty("delta", out var md)
                                    && md.TryGetProperty("stop_reason", out var sr)
                                    && sr.ValueKind == JsonValueKind.String)
                                {
                                    setStopReason(sr.GetString());
                                }
                                if (root.TryGetProperty("usage", out var u1))
                                {
                                    var output = ReadInt(u1, "output_tokens");
                                    if (output > 0) { usage.OutputTokens += output; setHasUsage(true); }
                                }
                                break;

                            case "message_stop":
                                // Nothing more on the wire after this.
                                break;

                            case "error":
                                if (root.TryGetProperty("error", out var err)
                                    && err.TryGetProperty("message", out var msg))
                                {
                                    RaiseError("Claude API: " + msg.GetString());
                                }
                                break;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    ExtensionLogger.Warn(AdapterId, "SSE parse error: " + data, ex);
                }
            }
        }

        // Internal representation of one message in the tool-use loop.
        // Always uses a content-block array so tool_use / tool_result
        // blocks can round-trip through the conversation.
        private sealed class ApiMessage
        {
            public string Role { get; set; } = "user";
            public List<ApiBlock> Blocks { get; set; } = new List<ApiBlock>();
        }

        // Tagged-union for the four block kinds we care about. Only the
        // fields relevant to Type are populated; the rest are ignored by
        // WriteBlock.
        private sealed class ApiBlock
        {
            public string Type { get; set; } = "text";
            public string Text { get; set; } = string.Empty;        // text / thinking / tool_result
            public string Id { get; set; } = string.Empty;          // tool_use
            public string Name { get; set; } = string.Empty;        // tool_use
            public string InputJson { get; set; } = string.Empty;   // tool_use (complete JSON)
            public string ToolUseId { get; set; } = string.Empty;   // tool_result
            public bool IsError { get; set; }                        // tool_result
        }

        // Accepts Int32 / Int64 / Double shapes — defensive against backends
        // that occasionally emit counts as floats, which would make a naked
        // GetInt32 call throw and we'd log-and-drop the whole SSE event.
        private static int ReadInt(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var v)) return 0;
            if (v.ValueKind != JsonValueKind.Number) return 0;
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetInt64(out var l)) return l > int.MaxValue ? int.MaxValue : (int)l;
            if (v.TryGetDouble(out var d)) return d > int.MaxValue ? int.MaxValue : (int)d;
            return 0;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
