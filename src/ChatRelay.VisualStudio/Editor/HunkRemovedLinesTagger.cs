using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatRelay.Host;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>Renders the "removed lines" inline red strip via VS's <see cref="InterLineAdornmentTag"/> primitive.</summary>
[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
[TagType(typeof(InterLineAdornmentTag))]
public sealed class HunkRemovedLinesTaggerProvider : IViewTaggerProvider
{
    [Import]
    public IClassificationFormatMapService FormatMapService { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView is not IWpfTextView wpf) return null;
        if (textView.TextBuffer != buffer) return null;
        var tagger = wpf.Properties.GetOrCreateSingletonProperty(
            typeof(HunkRemovedLinesTagger),
            () => new HunkRemovedLinesTagger(wpf, FormatMapService.GetClassificationFormatMap(wpf)));
        return tagger as ITagger<T>;
    }
}

internal sealed class HunkRemovedLinesTagger : ITagger<InterLineAdornmentTag>
{
    static readonly Brush RemovedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40)));
    static readonly Brush RemovedText = Frozen(new SolidColorBrush(Color.FromRgb(0xCB, 0x6E, 0x6E)));

    const double VerticalPadding = 2;
    const double MinLineHeight = 14;

    readonly IWpfTextView _view;
    readonly IClassificationFormatMap _formatMap;
    readonly EditorChangesService _service;
    readonly string? _filePath;

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public HunkRemovedLinesTagger(IWpfTextView view, IClassificationFormatMap formatMap)
    {
        _view = view;
        _formatMap = formatMap;
        _service = EditorChangesService.Current;
        _filePath = ResolveFilePath(view);
        if (_filePath is null) return;
        _service.HunksChanged += OnHunksChanged;
        _formatMap.ClassificationFormatMappingChanged += OnFormatMappingChanged;
        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnViewClosed;
    }

    public IEnumerable<ITagSpan<InterLineAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (_filePath is null || spans.Count == 0) yield break;
        var hunks = _service.GetHunks(_filePath);
        if (hunks.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var lineHeight = Math.Max(_view.LineHeight > 0 ? _view.LineHeight : 16, MinLineHeight);

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            int anchorLine = h.CurrentStart;
            if (anchorLine < 0 || anchorLine >= snapshot.LineCount) continue;

            // Anchor on N-1 reserving bottom-space (or above line 0 as fallback): VS CodeLens claims top-space on N,
            // and competing for the same gap squashed our first row underneath the CodeLens row.
            SnapshotPoint anchorPos;
            bool isAboveLine;
            if (anchorLine > 0)
            {
                anchorPos = snapshot.GetLineFromLineNumber(anchorLine - 1).Start;
                isAboveLine = false;
            }
            else
            {
                anchorPos = snapshot.GetLineFromLineNumber(anchorLine).Start;
                isAboveLine = true;
            }

            var tagSpan = new SnapshotSpan(anchorPos, 0);
            if (!AnyIntersects(spans, tagSpan)) continue;

            double height = h.BaselineLines.Count * lineHeight + VerticalPadding * 2;
            var captured = h;
            InterLineAdornmentFactory factory = (tag, view, position) => BuildAdornment(captured, tag.Height);

            yield return new TagSpan<InterLineAdornmentTag>(
                tagSpan,
                new InterLineAdornmentTag(
                    adornmentFactory: factory,
                    isAboveLine: isAboveLine,
                    initialHeight: height,
                    horizontalPositioningMode: HorizontalPositioningMode.ViewRelative,
                    horizontalOffset: 0,
                    removalCallback: null));
        }
    }

    // Strip is content-only; line numbers live in HunkRemovedLineNumberMargin so they aren't clipped at view-coord 0.
    UIElement BuildAdornment(HunkInfo h, double tagHeight)
    {
        var defaults = _formatMap.DefaultTextProperties;
        var typeface = defaults.Typeface;

        var content = new TextBox
        {
            Text = string.Join(Environment.NewLine, h.BaselineLines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, VerticalPadding, 4, VerticalPadding),
            FontFamily = typeface.FontFamily,
            FontStyle = typeface.Style,
            FontWeight = typeface.Weight,
            FontStretch = typeface.Stretch,
            FontSize = defaults.FontRenderingEmSize,
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
            Child = content,
            Background = RemovedFill,
            BorderThickness = new Thickness(0),
            Height = tagHeight,
            // Span the full viewport so the red bar matches the blue highlight's width.
            Width = _view.ViewportWidth,
        };
    }

    void OnHunksChanged(string changedPath)
    {
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        FireTagsChangedForWholeSnapshot();
    }

    void OnFormatMappingChanged(object? sender, EventArgs e) => FireTagsChangedForWholeSnapshot();

    double _lastViewportWidth;
    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        if (Math.Abs(_view.ViewportWidth - _lastViewportWidth) < 0.5) return;
        _lastViewportWidth = _view.ViewportWidth;
        FireTagsChangedForWholeSnapshot();
    }

    void FireTagsChangedForWholeSnapshot()
    {
        var snapshot = _view.TextSnapshot;
        if (snapshot.Length == 0) return;
        try { TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length))); }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] TagsChanged fire failed: {ex.Message}"); }
    }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _formatMap.ClassificationFormatMappingChanged -= OnFormatMappingChanged;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnViewClosed;
    }

    static bool AnyIntersects(NormalizedSnapshotSpanCollection spans, SnapshotSpan needle)
    {
        foreach (var s in spans)
            if (s.IntersectsWith(needle)) return true;
        return false;
    }

    static string? ResolveFilePath(IWpfTextView view) =>
        view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc) ? doc.FilePath : null;

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
