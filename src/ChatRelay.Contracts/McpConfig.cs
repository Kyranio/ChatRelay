using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatRelay.Settings;

/// <summary>
/// In-memory representation of a parsed ChatRelay <c>.chatrelay.mcp.json</c>
/// file. One entry per server under <see cref="McpServers"/>. Parsing /
/// merging behaviour lives in <c>ChatRelay.Core/Mcp/McpConfigService</c> —
/// this file only declares the shape.
/// </summary>
public class McpConfig
{
    /// <summary>Dictionary keyed by server name. Empty when no servers are configured.</summary>
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerEntry> McpServers { get; set; }
        = new Dictionary<string, McpServerEntry>();
}

/// <summary>
/// One MCP server entry. Covers the stdio + remote transport variants
/// used by Claude / the official MCP spec. Unknown fields pasted in
/// from a <c>.chatrelay.mcp.json</c> get dropped on round-trip — the common
/// fields below are what the CLI cares about.
/// </summary>
public class McpServerEntry
{
    /// <summary>"stdio" (default) / "sse" / "http". Null is equivalent to stdio.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Executable for stdio-transport servers (e.g. <c>npx</c>, <c>node</c>).</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Arguments passed to <see cref="Command"/>.</summary>
    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    /// <summary>Environment variables for the server process.</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Endpoint URL for sse / http-transport servers.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Extra HTTP headers for sse / http-transport servers.</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}
