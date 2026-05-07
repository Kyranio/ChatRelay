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
        // Skip projections / peek windows.
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
    static readonly Brush RemovedGutterText = Frozen(new SolidColorBrush(Color.FromRgb(0x96, 0x55, 0x55)));

    const double VerticalPadding = 2;
    // Defensive floor — _view.LineHeight should match WPF rendering once we adopt the editor's typeface.
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
        // Refresh on Tools > Options > Fonts and Colors changes.
        _formatMap.ClassificationFormatMappingChanged += OnFormatMappingChanged;
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

        double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? 0;
        if (columnWidth <= 0)
            columnWidth = _formatMap.DefaultTextProperties.FontRenderingEmSize * 0.6;

        // Shift left so content text aligns with tab-indented source code inside method bodies.
        double leftShift = -2 * columnWidth;

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            // Accepted hunks get only the marker bar (in HunkAdornmentManager), no inline strip.
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
                    // TextRelative scrolls with the source code; ViewRelative would pin to the viewport's left edge.
                    horizontalPositioningMode: HorizontalPositioningMode.TextRelative,
                    horizontalOffset: leftShift,
                    removalCallback: null));
        }
    }

    UIElement BuildAdornment(HunkInfo h, double tagHeight)
    {
        // Editor's actual typeface + size so the strip looks like a real source line.
        var defaults = _formatMap.DefaultTextProperties;
        var typeface = defaults.Typeface;
        double fontSize = defaults.FontRenderingEmSize;

        // Two columns: dim line-number gutter + selectable line-content TextBox.
        var lineNumbers = new System.Text.StringBuilder();
        for (int i = 0; i < h.BaselineLines.Count; i++)
        {
            if (i > 0) lineNumbers.Append('\n');
            lineNumbers.Append((h.BaselineStart + i + 1).ToString());
        }

        var gutter = new TextBlock
        {
            Text = lineNumbers.ToString(),
            FontFamily = typeface.FontFamily,
            FontStyle = typeface.Style,
            FontWeight = typeface.Weight,
            FontStretch = typeface.Stretch,
            FontSize = fontSize,
            Foreground = RemovedGutterText,
            TextAlignment = TextAlignment.Right,
            // V-padding matches the TextBox below so rows align.
            Padding = new Thickness(4, VerticalPadding, 4, VerticalPadding),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var content = new TextBox
        {
            Text = string.Join(Environment.NewLine, h.BaselineLines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            // V-padding matches the gutter so rows align; gutter's own R-padding gives breathing room.
            Padding = new Thickness(0, VerticalPadding, 4, VerticalPadding),
            FontFamily = typeface.FontFamily,
            FontStyle = typeface.Style,
            FontWeight = typeface.Weight,
            FontStretch = typeface.Stretch,
            FontSize = fontSize,
            Opacity = 0.95,
            IsTabStop = false,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = RemovedText,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(content);

        // No fixed Width — Border sizes to content so the strip scales like a real source line.
        return new Border
        {
            Child = grid,
            Background = RemovedFill,
            BorderThickness = new Thickness(0),
            Height = tagHeight,
        };
    }

    void OnHunksChanged(string changedPath)
    {
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        FireTagsChangedForWholeSnapshot();
    }

    void OnFormatMappingChanged(object? sender, EventArgs e) =>
        FireTagsChangedForWholeSnapshot();

    // Invalidate every tag — the host only tells us a path changed, not which lines.
    void FireTagsChangedForWholeSnapshot()
    {
        var snapshot = _view.TextSnapshot;
        if (snapshot.Length == 0) return;
        try { TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length))); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] TagsChanged fire failed: {ex.Message}");
        }
    }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _formatMap.ClassificationFormatMappingChanged -= OnFormatMappingChanged;
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
