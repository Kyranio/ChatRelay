using System;
using System.ComponentModel;

namespace ChatRelay.Chat.Models;

public sealed class ChatSession : INotifyPropertyChanged
{
    string _label = string.Empty;
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

    public string Id { get; set; } = string.Empty;
    public string? AdapterId { get; set; }
    public string? ModelId { get; set; }

    /// <summary>Timestamp of the most recent user/assistant message in this session, or null if none yet.</summary>
    public DateTime? LastMessageAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
