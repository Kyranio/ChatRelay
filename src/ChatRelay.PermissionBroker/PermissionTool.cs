using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ChatRelay.PermissionBroker;

/// <summary>
/// Single MCP tool the Claude CLI calls via <c>--permission-prompt-tool</c>
/// to ask whether a pending tool use should proceed. We tunnel the decision
/// to the host VS extension over a named pipe (name comes from the
/// <c>CLAUDEVS_PIPE</c> env var the extension sets on this broker's MCP
/// server entry) and return the resulting allow/deny payload in the exact
/// shape the CLI expects: a JSON-stringified
/// <c>{ "behavior": "allow" | "deny", "message"?: "..." }</c>.
/// </summary>
[McpServerToolType]
public sealed class PermissionTool
{
    private const int PipeConnectTimeoutMs = 3000;

    [McpServerTool(Name = "approve")]
    [Description("Route a Claude Code tool-use approval request to the host IDE for user confirmation.")]
    public static async Task<string> Approve(
        [Description("Name of the tool requesting approval (e.g. \"Bash\", \"Edit\").")] string tool_name,
        [Description("Raw input arguments the tool wants to run with.")] JsonElement input,
        [Description("Optional correlation id from the CLI.")] string? tool_use_id = null)
    {
        var pipeName = Environment.GetEnvironmentVariable("CLAUDEVS_PIPE");
        if (string.IsNullOrEmpty(pipeName))
        {
            return Deny("Extension pipe not configured (CLAUDEVS_PIPE missing).");
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(PipeConnectTimeoutMs);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            // Extension expects one line per request, JSON-encoded.
            var requestJson = JsonSerializer.Serialize(new
            {
                toolName = tool_name,
                toolUseId = tool_use_id ?? string.Empty,
                input
            });
            await writer.WriteLineAsync(requestJson);

            var replyLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(replyLine))
                return Deny("Extension closed pipe without answering.");

            return TranslateReply(replyLine, input);
        }
        catch (TimeoutException)
        {
            return Deny("Extension did not accept the pipe connection in time.");
        }
        catch (Exception ex)
        {
            return Deny("Broker IO error: " + ex.Message);
        }
    }

    // The extension sends us { decision: "allow"|"deny", message?: ... }.
    // The CLI expects the discriminated-union shape the permission-prompt
    // tool contract demands:
    //   Allow: { "behavior": "allow", "updatedInput": <the original tool input object> }
    //   Deny:  { "behavior": "deny",  "message": "<reason>" }
    // `updatedInput` is mandatory on allow — it's where a permission tool
    // can sanitize or override args. We don't alter anything today, so we
    // echo back the input verbatim.
    private static string TranslateReply(string line, JsonElement originalInput)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var allow = root.TryGetProperty("decision", out var d)
                && d.ValueKind == JsonValueKind.String
                && string.Equals(d.GetString(), "allow", StringComparison.Ordinal);

            string? message = null;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();

            return allow ? Allow(originalInput) : Deny(message ?? "Denied by user.");
        }
        catch (JsonException)
        {
            return Deny("Malformed extension reply.");
        }
    }

    private static string Allow(JsonElement originalInput)
    {
        // Use Utf8JsonWriter directly so we can embed the original input
        // JsonElement as a nested object without round-tripping through a
        // string.
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("behavior", "allow");
            w.WritePropertyName("updatedInput");
            if (originalInput.ValueKind == JsonValueKind.Object
                || originalInput.ValueKind == JsonValueKind.Array)
            {
                originalInput.WriteTo(w);
            }
            else
            {
                // Fallback — the schema requires an object. Empty object is
                // accepted by Zod's record validator.
                w.WriteStartObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Deny(string reason)
        => JsonSerializer.Serialize(new { behavior = "deny", message = reason }, SerializerOptions);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
