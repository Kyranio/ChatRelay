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
    [Import]
    public IClassificationFormatMapService FormatMapService { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView is not IWpfTextView wpf) return null;
        // Only attach to the view's primary buffer (skip projections / peek).
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
    // Dimmer than RemovedText so the gutter line numbers don't compete
    // with the actual code content for visual weight.
    static readonly Brush RemovedGutterText = Frozen(new SolidColorBrush(Color.FromRgb(0x96, 0x55, 0x55)));

    const double VerticalPadding = 2;

    // Defensive floor for per-line reserved height. Once we adopt the
    // editor's typeface + size below, _view.LineHeight should match the
    // WPF rendering height naturally, but a small floor protects against
    // weirdly-small font configurations.
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
        // Re-fire TagsChanged when the user changes Tools > Options > Fonts
        // and Colors so the inline block picks up the new typeface.
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

        // Shift width
        double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? 0;
        if (columnWidth <= 0)
            columnWidth = _formatMap.DefaultTextProperties.FontRenderingEmSize * 0.6;

        double leftShift = -2 * columnWidth;

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
                    // TextRelative anchors the adornment to the character at
                    // the tag's position (col 0 of the anchor line). When
                    // the user scrolls horizontally, view-coord 0 moves
                    // along with the source code, so the red strip moves
                    // with the text — the same behaviour real lines have.
                    // ViewRelative used to lock the strip to the viewport's
                    // left edge, which kept it stationary while the text
                    // scrolled out from under it.
                    horizontalPositioningMode: HorizontalPositioningMode.TextRelative,
                    horizontalOffset: leftShift,
                    removalCallback: null));
        }
    }

    UIElement BuildAdornment(HunkInfo h, double tagHeight)
    {
        // Pick up the editor's actual typeface + size so the inline block
        // looks like a real source line in whatever font the user has
        // configured. DefaultTextProperties carries the editor's plain-text
        // formatting (font family, weight, style, render-em size).
        var defaults = _formatMap.DefaultTextProperties;
        var typeface = defaults.Typeface;
        double fontSize = defaults.FontRenderingEmSize;

        // Two-column layout: a dim gutter showing the ORIGINAL line numbers
        // (1-based) of each removed line + the line content itself. Helps
        // distinguish multiple red strips that sit close together — without
        // numbers it's not always obvious which removed line belongs to
        // which hunk.
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
            // Top/bottom must match the TextBox padding so rows align.
            // Tightened L/R from 6 to 4 to pull the content text ~1 char
            // closer to the strip's left edge — was indented further than
            // the source code on the lines around it.
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
            // Top/bottom must match the gutter Padding so rows align.
            // No left-padding — the gutter's own right-padding already
            // gives breathing room between numbers and code.
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

        // Don't pin Width — the Border sizes to its content. Combined with
        // TextRelative tag positioning, this makes the strip behave like a
        // real source line: as wide as its content, scrolls with the
        // editor when the user pans horizontally.
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

    void FireTagsChangedForWholeSnapshot()
    {
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
