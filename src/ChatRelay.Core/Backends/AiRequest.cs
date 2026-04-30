using System.Collections.Generic;
using ChatRelay.Mcp;

namespace ChatRelay.Backends
{
    public enum AiTurnRole { User, Assistant }

    /// <summary>One prior turn handed to stateless adapters so they can reconstruct context.</summary>
    public class AiTurn
    {
        public AiTurnRole Role { get; set; }
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// One send-prompt call. Stateful adapters (Claude CLI) read
    /// <see cref="SessionId"/> and ignore <see cref="History"/>; stateless
    /// adapters (Claude API, Ollama) read the history and ignore session id.
    /// </summary>
    public class AiRequest
    {
        /// <summary>The user message to send on this turn (already includes any pinned-reference preamble).</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Prior turns in this conversation, oldest first. May be empty on the first turn.</summary>
        public IReadOnlyList<AiTurn> History { get; set; } = new List<AiTurn>();

        /// <summary>Adapter-opaque session id for stateful adapters; null to start fresh.</summary>
        public string? SessionId { get; set; }

        /// <summary>Adapter-specific model id (from <see cref="AiModel.Id"/>). Null means "adapter default".</summary>
        public string? Model { get; set; }

        /// <summary>CLI-style permission-mode hint. Adapters ignore it unless <see cref="AiCapabilities.PermissionModes"/> is true.</summary>
        public string? PermissionMode { get; set; }

        /// <summary>Tool patterns pre-approved for this send (CLI <c>--allowedTools</c>). Null/empty omits the flag.</summary>
        public IReadOnlyList<string>? AllowedTools { get; set; }

        /// <summary>Tool patterns always refused (CLI <c>--disallowedTools</c>). Null/empty omits the flag.</summary>
        public IReadOnlyList<string>? DisallowedTools { get; set; }

        /// <summary>
        /// Fully-qualified MCP tool id for <c>--permission-prompt-tool</c>
        /// (e.g. <c>mcp__cvs-permissions__approve</c>). Set when we want the
        /// CLI to call back into the extension's broker rather than emit a
        /// TTY permission prompt. Null omits the flag.
        /// </summary>
        public string? PermissionPromptTool { get; set; }

        /// <summary>
        /// Optional path to a merged MCP server config (<c>.chatrelay.mcp.json</c>-
        /// shaped). Only consumed by the Claude CLI adapter today; other
        /// adapters ignore it. Caller owns the file's lifetime — the CLI
        /// reads it synchronously during send, so a follow-up delete after
        /// send is safe.
        /// </summary>
        public string? McpConfigPath { get; set; }

        /// <summary>
        /// Optional working directory to spawn subprocess-based backends in
        /// (Claude CLI today). Usually the solution directory so project-
        /// relative paths the model produces resolve against the user's
        /// code rather than the VS install root.
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Extra directories the CLI should allow tool access to, on top of
        /// <see cref="WorkingDirectory"/>. Each entry becomes one
        /// <c>--add-dir &lt;path&gt;</c>. Null/empty omits the flag entirely.
        /// </summary>
        public IReadOnlyList<string>? AdditionalDirectories { get; set; }

        /// <summary>
        /// Cross-adapter MCP host. Adapters with a native tool-use
        /// protocol (Claude API, Ollama) pull tool schemas from
        /// <see cref="IMcpRuntime.ListAvailableTools"/>, inject them into
        /// the model's tool list, and dispatch any resulting tool calls
        /// through <see cref="IMcpRuntime.CallToolAsync"/>. Claude CLI
        /// sidesteps all of that via <see cref="McpConfigPath"/> — the CLI
        /// does its own MCP dance. Null when no MCP servers are configured
        /// or when the adapter doesn't support MCP.
        /// </summary>
        public IMcpRuntime? Mcp { get; set; }
    }
}
