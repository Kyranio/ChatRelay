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
}
