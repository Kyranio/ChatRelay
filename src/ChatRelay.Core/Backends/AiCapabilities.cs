namespace ChatRelay.Backends
{
    /// <summary>
    /// Per-adapter feature flags. The host uses these to decide which
    /// fields of <see cref="AiRequest"/> are worth populating and whether
    /// to send conversation history (stateless adapters) or rely on
    /// server-side continuity (<c>--resume</c>-style stateful adapters).
    /// </summary>
    public class AiCapabilities
    {
        /// <summary>True if the adapter tracks server-side sessions and can resume them by id.</summary>
        public bool StatefulSessions { get; set; }

        /// <summary>
        /// True for adapters that run as a sandboxed CLI subprocess and route
        /// tool-use approvals through the permission broker (Claude CLI only,
        /// currently). Gates the workspace-scoped fields on
        /// <see cref="AiRequest"/> (<c>WorkingDirectory</c>,
        /// <c>AdditionalDirectories</c>, <c>AllowedTools</c>,
        /// <c>DisallowedTools</c>, <c>PermissionPromptTool</c>,
        /// <c>McpConfigPath</c>). Stateless API-shaped adapters leave it false.
        /// </summary>
        public bool PermissionModes { get; set; }

        /// <summary>True if SendPromptAsync streams assistant text in chunks rather than all-at-once.</summary>
        public bool Streaming { get; set; }
    }
}
