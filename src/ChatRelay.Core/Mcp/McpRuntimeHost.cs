namespace ChatRelay.Mcp
{
    /// <summary>
    /// Process-wide singleton for the MCP runtime. Previously each UI
    /// surface (chat tool menu, settings MCP tab) owned its own
    /// <see cref="McpServerManager"/>, which meant:
    ///   • Starting a server in the menu wouldn't show as "running" in
    ///     the settings window.
    ///   • Stopping a server in the settings window wouldn't hide its
    ///     tools in the menu — or from the model.
    ///   • Each UI spawned its own copies of the same server process.
    /// Sharing one runtime means both windows observe the same state
    /// via <see cref="IMcpRuntime.Servers"/> + <see cref="System.ComponentModel.INotifyPropertyChanged"/>,
    /// and the adapters' send-time tool dispatch hits the same processes.
    ///
    /// Lifetime: created lazily on first access; lives for the VS session.
    /// Child processes are killed on <see cref="Shutdown"/> (called from
    /// the package's dispose path) so stopping VS doesn't orphan MCP
    /// servers. Inside a session the runtime is never re-created; a
    /// chat tool window closing and reopening reuses the same handles.
    /// </summary>
    public static class McpRuntimeHost
    {
        private static McpRuntime? _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// The shared runtime. Thread-safe lazy init so concurrent tool
        /// windows / settings windows / adapter sends all land on the
        /// same instance.
        /// </summary>
        public static IMcpRuntime Instance
        {
            get
            {
                var snapshot = _instance;
                if (snapshot != null) return snapshot;
                lock (_lock)
                {
                    if (_instance == null) _instance = new McpRuntime();
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Tear down the shared runtime — stops every running MCP server
        /// subprocess. Safe to call multiple times (idempotent).
        /// Intended to run from the <c>AsyncPackage.Dispose</c> path so
        /// VS shutdown doesn't leave orphan child processes.
        /// </summary>
        public static void Shutdown()
        {
            McpRuntime? toDispose;
            lock (_lock)
            {
                toDispose = _instance;
                _instance = null;
            }
            toDispose?.Dispose();
        }
    }
}
