namespace ChatRelay.Backends
{
    /// <summary>
    /// Per-adapter feature flags. The control uses these to show/hide UI bits
    /// (permission-mode combo) and to decide whether to send conversation
    /// history (stateless adapters) or rely on server-side continuity
    /// (<c>--resume</c>-style stateful adapters).
    /// </summary>
    public class AiCapabilities
    {
        /// <summary>True if the adapter tracks server-side sessions and can resume them by id.</summary>
        public bool StatefulSessions { get; set; }

        /// <summary>True if the adapter honours a permission-mode hint (Claude CLI only, currently).</summary>
        public bool PermissionModes { get; set; }

        /// <summary>True if SendPromptAsync streams assistant text in chunks rather than all-at-once.</summary>
        public bool Streaming { get; set; }
    }
}
