using System.Text.Json;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Metadata for one tool advertised by an MCP server. Populated from
    /// the server's <c>tools/list</c> response during the handshake.
    /// Carries enough information for both the tool-gate menu (name +
    /// description) and the cross-adapter tool-use loop
    /// (<see cref="InputSchema"/> is handed straight to the LLM as the
    /// tool's JSON schema).
    /// </summary>
    public sealed class McpToolInfo
    {
        public string Name { get; }

        /// <summary>Human-readable description from the tool's schema. May be null when the server omits it.</summary>
        public string? Description { get; }

        /// <summary>
        /// JSON schema object describing the tool's input shape. Cloned
        /// from the server's response so the lifetime is independent of
        /// the underlying JsonDocument. Default (ValueKind == Undefined)
        /// when the server omits <c>inputSchema</c>.
        /// </summary>
        public JsonElement InputSchema { get; }

        public McpToolInfo(string name, string? description, JsonElement inputSchema)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
        }
    }
}
