using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Shared base for every <see cref="IAiAdapter"/> implementation. Hoists
    /// the event plumbing — declaration of the two events + protected
    /// <c>Raise*</c> helpers — so concrete adapters focus on the
    /// backend-specific protocol rather than re-implementing "fire the
    /// handler under a null-check" five times.
    /// </summary>
    public abstract class AiAdapterBase : IAiAdapter
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public abstract AiCapabilities Capabilities { get; }

        public event EventHandler<AiMessageEvent>? MessageReceived;
        public event EventHandler<AiErrorEvent>? ErrorReceived;
        public event EventHandler<ToolCallObservedEvent>? ToolCallObserved;

        public abstract Task<bool> IsAvailableAsync(CancellationToken ct);
        public abstract Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct);
        public abstract Task SendPromptAsync(AiRequest request, CancellationToken ct);

        /// <summary>Fire a message event — no-op when nobody's listening.</summary>
        protected void RaiseMessage(AiMessageEvent evt)
            => MessageReceived?.Invoke(this, evt);

        /// <summary>Convenience for the common assistant-text case.</summary>
        protected void RaiseAssistantMessage(string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            RaiseMessage(new AiMessageEvent { Kind = AiEventKind.AssistantMessage, Content = content });
        }

        /// <summary>Convenience for the thinking/reasoning block.</summary>
        protected void RaiseThinkingMessage(string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            RaiseMessage(new AiMessageEvent { Kind = AiEventKind.ThinkingMessage, Content = content });
        }

        /// <summary>Convenience for the model-info event emitted on handshake.</summary>
        protected void RaiseModelInfo(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return;
            RaiseMessage(new AiMessageEvent { Kind = AiEventKind.ModelInfo, ModelDisplayName = displayName });
        }

        /// <summary>Convenience for the session-id update (stateful adapters).</summary>
        protected void RaiseSessionUpdate(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            RaiseMessage(new AiMessageEvent { Kind = AiEventKind.SessionUpdate, SessionId = sessionId });
        }

        /// <summary>Convenience for the per-turn usage event.</summary>
        protected void RaiseUsage(AiUsage usage)
        {
            if (usage == null) return;
            RaiseMessage(new AiMessageEvent { Kind = AiEventKind.UsageUpdate, Usage = usage });
        }

        /// <summary>Fire an error event with the given human-readable message.</summary>
        protected void RaiseError(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            ErrorReceived?.Invoke(this, new AiErrorEvent { Message = message });
        }

        /// <summary>Fire a tool-call observation. No-op when nobody's listening.</summary>
        protected void RaiseToolCall(string toolName, string inputJson, ToolCallPhase phase, string callId = "")
        {
            if (string.IsNullOrEmpty(toolName)) return;
            ToolCallObserved?.Invoke(this, new ToolCallObservedEvent
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName,
                InputJson = inputJson ?? string.Empty,
                Phase = phase,
            });
        }
    }
}
