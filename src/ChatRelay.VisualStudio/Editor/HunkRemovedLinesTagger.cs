using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatRelay.Host;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// Renders the "removed lines" inline block via VS's
/// <see cref="InterLineAdornmentTag"/> primitive — VS reserves the gap
/// AND positions the adornment for us, so we don't have to manage
/// LineTransform reservations or Canvas Y coordinates ourselves.
/// </summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
[TagType(typeof(InterLineAdornmentTag))]
public sealed class HunkRemovedLinesTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView is not IWpfTextView wpf) return null;
        // Only attach to the view's primary buffer (skip projections / peek).
        if (textView.TextBuffer != buffer) return null;
        var tagger = wpf.Properties.GetOrCreateSingletonProperty(
            typeof(HunkRemovedLinesTagger),
            () => new HunkRemovedLinesTagger(wpf));
        return tagger as ITagger<T>;
    }
}

internal sealed class HunkRemovedLinesTagger : ITagger<InterLineAdornmentTag>
{
    static readonly Brush RemovedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40)));
    static readonly Brush RemovedText = Frozen(new SolidColorBrush(Color.FromRgb(0xCB, 0x6E, 0x6E)));

    const double VerticalPadding = 2;

    // Floor for per-line reserved height. _view.LineHeight is the editor's
    // glyph height; our WPF TextBox at FontSize 12 needs ~16-18px per
    // line. Without a floor, multi-line removed-blocks clip everything
    // past line 1 inside the gap.
    const double MinLineHeight = 18;

    readonly IWpfTextView _view;
    readonly EditorChangesService _service;
    readonly string? _filePath;

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public HunkRemovedLinesTagger(IWpfTextView view)
    {
        _view = view;
        _service = EditorChangesService.Current;
        _filePath = ResolveFilePath(view);
        if (_filePath is null) return;
        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;
    }

    public IEnumerable<ITagSpan<InterLineAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (_filePath is null || spans.Count == 0) yield break;

        var hunks = _service.GetHunks(_filePath);
        if (hunks.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var editorLineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
        var lineHeight = Math.Max(editorLineHeight, MinLineHeight);

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            // Accepted hunks render only as the marker bar (separately, in
            // HunkAdornmentManager); no inline removed-block.
            if (string.Equals(h.State, "accepted", StringComparison.Ordinal)) continue;

            int anchorLine = h.CurrentStart;
            if (anchorLine < 0 || anchorLine >= snapshot.LineCount) continue;

            var anchorPos = snapshot.GetLineFromLineNumber(anchorLine).Start;
            var tagSpan = new SnapshotSpan(anchorPos, 0);
            if (!AnyIntersects(spans, tagSpan)) continue;

            double height = h.BaselineLines.Count * lineHeight + VerticalPadding * 2;
            var captured = h;
            InterLineAdornmentFactory factory = (tag, view, position) =>
                BuildAdornment(captured, tag.Height);

            yield return new TagSpan<InterLineAdornmentTag>(
                tagSpan,
                new InterLineAdornmentTag(
                    adornmentFactory: factory,
                    isAboveLine: true,
                    initialHeight: height,
                    horizontalPositioningMode: HorizontalPositioningMode.ViewRelative,
                    horizontalOffset: 0,
                    removalCallback: null));
        }
    }

    UIElement BuildAdornment(HunkInfo h, double tagHeight)
    {
        var text = new TextBox
        {
            Text = string.Join(Environment.NewLine, h.BaselineLines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, VerticalPadding, 4, VerticalPadding),
            FontFamily = new FontFamily("Consolas, Lucida Sans Typewriter, Courier New"),
            FontSize = 12,
            Opacity = 0.95,
            IsTabStop = false,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = RemovedText,
        };

        return new Border
        {
            Child = text,
            Background = RemovedFill,
            BorderThickness = new Thickness(0),
            Width = _view.ViewportWidth,
            Height = tagHeight,
        };
    }

    void OnHunksChanged(string changedPath)
    {
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        var snapshot = _view.TextSnapshot;
        if (snapshot.Length == 0) return;
        // Invalidate tags across the full snapshot — the host doesn't
        // tell us which lines changed, so let VS re-call GetTags for the
        // visible spans.
        try { TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length))); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] TagsChanged fire failed: {ex.Message}");
        }
    }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _view.Closed -= OnViewClosed;
    }

    static bool AnyIntersects(NormalizedSnapshotSpanCollection spans, SnapshotSpan needle)
    {
        foreach (var s in spans)
            if (s.IntersectsWith(needle)) return true;
        return false;
    }

    static string? ResolveFilePath(IWpfTextView view)
    {
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
