using System;

namespace ChatRelay.Backends
{
    public enum AiEventKind
    {
        /// <summary>Adapter learned or confirmed the session id; save it on the ChatSession.</summary>
        SessionUpdate,

        /// <summary>Adapter learned the human-friendly model name (e.g. "Claude Opus 4.5"). Update the nametag.</summary>
        ModelInfo,

        /// <summary>A complete assistant text block — render it as a bubble.</summary>
        AssistantMessage,

        /// <summary>A complete chunk of extended-thinking / reasoning content. Buffered and attached to the next AssistantMessage bubble.</summary>
        ThinkingMessage,

        /// <summary>Token/cost accounting for the just-finished turn. Stamp onto the most recent assistant bubble.</summary>
        UsageUpdate
    }

    public class AiMessageEvent : EventArgs
    {
        public AiEventKind Kind { get; set; }

        /// <summary>The assistant text, for <see cref="AiEventKind.AssistantMessage"/>.</summary>
        public string? Content { get; set; }

        /// <summary>The new session id, for <see cref="AiEventKind.SessionUpdate"/>.</summary>
        public string? SessionId { get; set; }

        /// <summary>The human-friendly model name, for <see cref="AiEventKind.ModelInfo"/>.</summary>
        public string? ModelDisplayName { get; set; }

        /// <summary>Token/cost accounting, for <see cref="AiEventKind.UsageUpdate"/>.</summary>
        public AiUsage? Usage { get; set; }
    }

    public class AiErrorEvent : EventArgs
    {
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Adapters fire this whenever the model emits or completes a tool call.
    /// The change tracker (host-side) is the primary consumer; future tool
    /// log / audit features can subscribe alongside.
    ///
    /// <para>
    /// <see cref="ToolCallPhase.Requested"/> fires when the model emits a
    /// <c>tool_use</c> content block — file writes haven't happened yet, so
    /// the tracker can snapshot the pre-write file content.
    /// <see cref="ToolCallPhase.Completed"/> fires when the corresponding
    /// <c>tool_result</c> arrives — the post-write state is now on disk.
    /// </para>
    /// </summary>
    public class ToolCallObservedEvent : EventArgs
    {
        public string ToolName { get; set; } = string.Empty;
        public string InputJson { get; set; } = string.Empty;
        public ToolCallPhase Phase { get; set; }
    }

    public enum ToolCallPhase
    {
        Requested,
        Completed,
    }
}
