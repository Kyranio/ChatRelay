using System.Collections.Generic;
using System.ComponentModel;

namespace ChatRelay.Chat
{
    /// <summary>
    /// One entry in the session dropdown. <see cref="SessionId"/> is null until
    /// the first prompt is sent — the CLI assigns one via its system/init event
    /// and <c>ChatControl</c> copies it back here after
    /// <c>SendPromptAsync</c> finishes so the session can be resumed later.
    /// State lives as plain data (not UIElements) so it persists across VS
    /// restarts: the control rebuilds bubbles from <see cref="Messages"/> when
    /// you switch into a session.
    /// </summary>
    public sealed class ChatSession : INotifyPropertyChanged
    {
        private string _label = string.Empty;
        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public string? SessionId { get; set; }

        /// <summary>
        /// Which adapter this session uses (<c>claude-cli</c>, <c>claude-api</c>,
        /// <c>ollama</c>, …). Null means "use the current default" — picked the
        /// first time the user sends. Persisted so a restored session keeps
        /// routing to the same backend.
        /// </summary>
        public string? AdapterId { get; set; }

        /// <summary>Adapter-specific model id the user picked (e.g. "opus", "claude-sonnet-4-5-20250929", "llama3.2:3b"). May be empty for adapter default.</summary>
        public string? ModelId { get; set; }

        /// <summary>Ordered chat-bubble data for this session.</summary>
        public List<PersistedBubble> Messages { get; } = new List<PersistedBubble>();

        /// <summary>Pinned references not yet sent.</summary>
        public List<ReferenceItem> References { get; } = new List<ReferenceItem>();

        /// <summary>In-progress draft in the input box.</summary>
        public string DraftText { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
