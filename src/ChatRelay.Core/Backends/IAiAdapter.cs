using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChatRelay.Backends
{
    /// <summary>
    /// A backend that can answer prompts: Claude CLI, Claude API, Ollama, etc.
    /// The registry probes every implementation on startup; adapters that
    /// report unavailable are hidden from the model dropdown.
    ///
    /// Events are raised from a background thread — callers must marshal to
    /// the UI thread before touching the visual tree.
    /// </summary>
    public interface IAiAdapter
    {
        /// <summary>Stable machine id (e.g. "claude-cli"). Used as the grouping key in the model dropdown.</summary>
        string Id { get; }

        /// <summary>Human-friendly name shown as the dropdown group header (e.g. "Claude CLI").</summary>
        string DisplayName { get; }

        /// <summary>Static flags the UI consults to show/hide adapter-specific controls.</summary>
        AiCapabilities Capabilities { get; }

        /// <summary>
        /// Cheap probe run on startup. Must never throw — return false on any
        /// failure (missing binary, firewall, no API key, etc.).
        /// </summary>
        Task<bool> IsAvailableAsync(CancellationToken ct);

        /// <summary>
        /// Enumerate models this adapter can route to. Called once after a
        /// successful <see cref="IsAvailableAsync"/>; failure returns empty.
        /// </summary>
        Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct);

        /// <summary>
        /// Send one prompt and stream events back. Honour <paramref name="ct"/>
        /// — when the user hits stop, the adapter must terminate in-flight I/O
        /// and throw <see cref="OperationCanceledException"/>.
        /// </summary>
        Task SendPromptAsync(AiRequest request, CancellationToken ct);

        event EventHandler<AiMessageEvent> MessageReceived;
        event EventHandler<AiErrorEvent> ErrorReceived;
    }
}
