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
/// <see cref="InterLineAdornmentTag"/> primitive — the same mechanism
/// CodeLens / inline references use to inject vertical space between
/// source rows. VS reserves the gap, picks the anchor line, and
/// positions the adornment for us. Replaces the earlier manual
/// <c>ILineTransformSource + adornment</c> approach which suffered
/// from race conditions between top-space updates and adornment
/// painting (multiple hunks would stack at stale Y positions until a
/// subsequent layout pass).
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
        // Per-buffer-view scoping — only attach if the requested buffer is
        // the view's primary buffer (not a projection / preview / etc.).
        if (textView.TextBuffer != buffer) return null;
        var tagger = wpf.Properties.GetOrCreateSingletonProperty(
            typeof(HunkRemovedLinesTagger),
            () => new HunkRemovedLinesTagger(wpf));
        return tagger as ITagger<T>;
    }
}

/// <summary>
/// Per-view tagger that emits one <see cref="InterLineAdornmentTag"/>
/// per open hunk that has removed-side content (model deleted lines).
/// VS handles space reservation and rendering placement automatically;
/// we just provide the tag's anchor SnapshotSpan, the height to
/// reserve, and a factory that creates the red TextBox.
/// </summary>
internal sealed class HunkRemovedLinesTagger : ITagger<InterLineAdornmentTag>
{
    // Translucent red — same hue family as the previous block style.
    static readonly Brush RemovedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40)));
    static readonly Brush RemovedText = Frozen(new SolidColorBrush(Color.FromRgb(0xCB, 0x6E, 0x6E)));

    // Vertical breathing room around the inline block's text.
    const double VerticalPadding = 2;

    // Per-line height used when reserving InterLineAdornmentTag space.
    // The editor's IWpfTextView.LineHeight reports the EDITOR's per-line
    // pixel height, but our adornment is a WPF TextBox using FontSize 12
    // which renders at ~16px/line regardless. Reserving editor-line-height
    // pixels leaves the TextBox short on space and only the first line
    // of multi-line content is visible — the rest gets clipped inside
    // the reserved gap. Reserve at least this many pixels per line so
    // the WPF rendering always fits.
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
        // Use whichever is taller: the editor's reported line height or
        // our floor for WPF TextBox rendering. Otherwise multi-line
        // removed-blocks clip everything past line 1.
        var editorLineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
        var lineHeight = Math.Max(editorLineHeight, MinLineHeight);

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            // Accepted hunks have no inline block — only the marker bar
            // (rendered separately by HunkAdornmentManager).
            if (string.Equals(h.State, "accepted", StringComparison.Ordinal)) continue;

            int anchorLine = h.CurrentStart;
            if (anchorLine < 0 || anchorLine >= snapshot.LineCount) continue;

            var anchorPos = snapshot.GetLineFromLineNumber(anchorLine).Start;
            // Tag span: zero-width point at the anchor line. VS uses this
            // to decide where to reserve the gap; the tag is included
            // when the requested span set intersects the point.
            var tagSpan = new SnapshotSpan(anchorPos, 0);
            bool inRange = false;
            foreach (var s in spans)
            {
                if (s.IntersectsWith(tagSpan)) { inRange = true; break; }
            }
            if (!inRange) continue;

            double height = h.BaselineLines.Count * lineHeight + VerticalPadding * 2;
            var captured = h;     // closure capture
            InterLineAdornmentFactory factory = (tag, view, position) =>
                BuildAdornment(captured, tag.Height);

            var iltag = new InterLineAdornmentTag(
                adornmentFactory: factory,
                isAboveLine: true,
                initialHeight: height,
                horizontalPositioningMode: HorizontalPositioningMode.ViewRelative,
                horizontalOffset: 0,
                removalCallback: null);

            yield return new TagSpan<InterLineAdornmentTag>(tagSpan, iltag);
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
            // Inner gutter so the lines don't hug the left edge or the
            // top/bottom of the red strip.
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
        if (_filePath is null) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        // Hunk set changed — invalidate all tags and let VS re-call
        // GetTags for the visible spans. The new tags will reflect the
        // updated hunks and VS will reposition / re-reserve gaps as
        // needed.
        var snapshot = _view.TextSnapshot;
        if (snapshot.Length == 0) return;
        var span = new SnapshotSpan(snapshot, 0, snapshot.Length);
        try { TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span)); }
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

    static string? ResolveFilePath(IWpfTextView view)
    {
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
