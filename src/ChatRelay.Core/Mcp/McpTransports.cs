using System;
using System.IO;
using ChatRelay.Settings;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Picks the right <see cref="IMcpTransport"/> implementation for one
    /// <see cref="McpServerEntry"/>. Single switch — adding a new transport
    /// (HTTP, SSE, WebSocket, …) means adding one case here and one new
    /// class implementing <see cref="IMcpTransport"/>; nothing else in the
    /// MCP feature changes.
    /// </summary>
    public static class McpTransports
    {
        /// <summary>
        /// Build a transport for the given server entry. Throws
        /// <see cref="NotSupportedException"/> if the entry's
        /// <see cref="McpServerEntry.Type"/> is unrecognised — the handle
        /// translates that into a user-visible Error status with the message.
        /// </summary>
        /// <param name="serverName">The user-visible server name, used for log channels.</param>
        /// <param name="config">The parsed entry from a .chatrelay.mcp.json file.</param>
        /// <param name="sourcePath">Absolute path to the file the entry came from. Used as the working-directory default for stdio servers.</param>
        public static IMcpTransport CreateFor(
            string serverName, McpServerEntry config, string sourcePath)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // Default: stdio. Most public MCP servers (npm, dotnet global
            // tools) ship as stdio executables and omit the type field.
            var type = string.IsNullOrEmpty(config.Type) ? "stdio" : config.Type!.ToLowerInvariant();

            return type switch
            {
                "stdio" => BuildStdio(serverName, config, sourcePath),

                // "http" and "sse" both map to Streamable HTTP — they're
                // configuration aliases for the same wire protocol per the
                // 2025-03-26 MCP spec. The transport detects single-shot
                // JSON vs SSE response per request from the response
                // Content-Type header.
                "http" or "sse" => BuildHttp(serverName, config),

                _ => throw new NotSupportedException(
                    $"Unsupported MCP transport \"{config.Type}\". " +
                    $"Supported: stdio, http, sse. (Add a case in " +
                    $"{nameof(McpTransports)}.{nameof(CreateFor)} to extend.)")
            };
        }

        private static IMcpTransport BuildStdio(
            string serverName, McpServerEntry config, string sourcePath)
        {
            if (string.IsNullOrEmpty(config.Command))
                throw new InvalidOperationException(
                    $"Stdio MCP server \"{serverName}\" has no command — set \"command\" in the config file.");

            var workingDir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
            return new StdioMcpTransport(
                serverName,
                config.Command!,
                config.Args,
                config.Env,
                workingDir);
        }

        private static IMcpTransport BuildHttp(string serverName, McpServerEntry config)
        {
            if (string.IsNullOrEmpty(config.Url))
                throw new InvalidOperationException(
                    $"Remote MCP server \"{serverName}\" has no url — set \"url\" in the config file.");

            return new HttpMcpTransport(serverName, config.Url!, config.Headers);
        }
    }
}
