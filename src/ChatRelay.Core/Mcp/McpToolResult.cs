namespace ChatRelay.Mcp
{
    /// <summary>
    /// Outcome of a single <c>tools/call</c> round-trip, flattened from the
    /// MCP content-block array into something the chat adapters can hand
    /// to the LLM as a tool-result payload. Spec allows a mix of text /
    /// image / resource blocks; we pass text through verbatim and render
    /// others as bracketed placeholders so the model at least knows the
    /// tool produced *something* even if we can't surface the media in a
    /// text channel.
    /// </summary>
    public sealed class McpToolResult
    {
        /// <summary>Mirrors the MCP <c>isError</c> flag. True when the tool signaled failure (bad args, runtime error, etc.).</summary>
        public bool IsError { get; set; }

        /// <summary>Flattened string content — joined text blocks plus placeholders for non-text content. Empty string when the tool produced nothing visible.</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>Convenience factory for the error path; callers can stay on one line.</summary>
        public static McpToolResult Error(string message) =>
            new McpToolResult { IsError = true, Content = message ?? string.Empty };
    }
}
