using System.Collections.Generic;
using System.ComponentModel;
using ChatRelay.Host;

namespace ChatRelay.Chat.Models
{
    /// <summary>One row in the chat-side changes list. INPC so the rendered chip updates live as line counts shift.</summary>
    public sealed class ChangeItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; } = string.Empty;
        public string AbsolutePath { get; set; } = string.Empty;

        int _linesAdded;
        public int LinesAdded
        {
            get => _linesAdded;
            set { if (_linesAdded != value) { _linesAdded = value; Raise(nameof(LinesAdded)); } }
        }

        int _linesRemoved;
        public int LinesRemoved
        {
            get => _linesRemoved;
            set { if (_linesRemoved != value) { _linesRemoved = value; Raise(nameof(LinesRemoved)); } }
        }

        int _acceptedLinesAdded;
        public int AcceptedLinesAdded
        {
            get => _acceptedLinesAdded;
            set { if (_acceptedLinesAdded != value) { _acceptedLinesAdded = value; Raise(nameof(AcceptedLinesAdded)); } }
        }

        int _acceptedLinesRemoved;
        public int AcceptedLinesRemoved
        {
            get => _acceptedLinesRemoved;
            set { if (_acceptedLinesRemoved != value) { _acceptedLinesRemoved = value; Raise(nameof(AcceptedLinesRemoved)); } }
        }

        public bool HasOpenChanges => _linesAdded > 0 || _linesRemoved > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One row in the collapsible "Denied / undone changes" section.
    /// Carries a stable id so the redo RPC has something to address even
    /// when the same file has multiple denials. <see cref="CanRedo"/>
    /// flips false once the file's drifted from the post-deny state —
    /// the redo button is hidden / dimmed in that case.
    /// </summary>
    public sealed class DenialItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string AbsolutePath { get; set; } = string.Empty;
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public System.DateTime DeniedAt { get; set; }

        bool _canRedo = true;
        public bool CanRedo
        {
            get => _canRedo;
            set { if (_canRedo != value) { _canRedo = value; Raise(nameof(CanRedo)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
