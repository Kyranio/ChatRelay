using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ChatRelay.Host;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace ChatRelay.Editor;

/// <summary>
/// Per-editor-view renderer for the turquoise highlight on open hunks
/// and the accept/reject button row. Listens to
/// <see cref="EditorChangesService.HunksChanged"/> for its file plus
/// <see cref="IWpfTextView.LayoutChanged"/> /
/// <see cref="ITextBuffer.Changed"/> /
/// <see cref="ITextDocument.FileActionOccurred"/>, repaints accordingly,
/// and routes button clicks back to the host via the service callbacks.
///
/// <para>
/// The "removed lines" red block isn't drawn here — that's
/// <see cref="HunkRemovedLinesTagger"/> producing
/// <c>InterLineAdornmentTag</c>s. VS handles the gap reservation and
/// adornment placement; we only render the things that anchor on
/// existing source lines.
/// </para>
/// </summary>
sealed class HunkAdornmentManager
{
    // Brand turquoise — matches the chat-side ClaudeButtonStyle's accent.
    static readonly Color AccentColor = Color.FromRgb(0x40, 0xE0, 0xD0);
    static readonly Brush AccentBrush = Frozen(new SolidColorBrush(AccentColor));
    static readonly Brush AccentBrushHover = Frozen(new SolidColorBrush(Color.FromRgb(0x5F, 0xE6, 0xD8)));
    static readonly Brush AccentBrushPressed = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x88, 0x81)));
    static readonly Brush HighlightFill = Frozen(new SolidColorBrush(Color.FromArgb(0x35, 0x40, 0xE0, 0xD0)));

    readonly IWpfTextView _view;
    // Lazy-resolved: ITextDocument (and therefore the buffer's FilePath)
    // sometimes attaches AFTER our IWpfTextViewCreationListener fires, so a
    // construction-time resolve would miss it permanently. We retry from
    // each event handler until it sticks. Mutable for that reason.
    string? _filePath;
    ITextDocument? _doc;
    readonly EditorChangesService _service;
    readonly IAdornmentLayer? _layer;
    readonly IAdornmentLayer? _overlay;

    // Tracked hunks carry an ITrackingSpan so the highlight/buttons
    // follow buffer edits live without waiting for a host snapshot.
    // LocallyInvalidated hides a hunk visually when the user has wiped
    // the model's content out of the tracked span (e.g. ctrl+Z) before
    // the host catches up on the next snapshot.
    readonly List<TrackedHunk> _tracked = new();

    sealed class TrackedHunk
    {
        public HunkInfo Info { get; set; } = null!;
        public ITrackingSpan Span { get; set; } = null!;
        public bool LocallyInvalidated { get; set; }
    }

    public HunkAdornmentManager(IWpfTextView view)
    {
        _view = view;
        _service = EditorChangesService.Current;
        try
        {
            _layer = view.GetAdornmentLayer(HunkAdornmentLayerDefinition.LayerName);
            _overlay = view.GetAdornmentLayer(HunkAdornmentLayerDefinition.OverlayLayerName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] Failed to acquire adornment layer: {ex.Message}");
            _layer = null;
            _overlay = null;
        }

        // Subscribe unconditionally — _filePath might still be null at
        // this point (ITextDocument hasn't attached yet) and we need the
        // event handlers to retry resolution when something later happens
        // on the view.
        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;
        _view.LayoutChanged += OnLayoutChanged;
        _view.TextBuffer.Changed += OnBufferChanged;

        // Best-effort initial resolve. If it works, EnsureFilePath also
        // fires the initial render. If not, the next event handler will
        // retry resolution.
        EnsureFilePath();
    }

    /// <summary>
    /// Resolves the buffer's file path lazily. Returns true once we have a
    /// path; false until ITextDocument attaches. Every event handler calls
    /// this first so the renderer comes online as soon as the document
    /// is available — even if that's well after IWpfTextViewCreationListener
    /// fired. The first time it resolves we also prime the renderer so any
    /// hunks already cached in EditorChangesService surface immediately.
    /// </summary>
    bool EnsureFilePath()
    {
        if (_filePath is not null) return true;
        if (!_view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return false;
        _doc = doc;
        _filePath = doc.FilePath;
        if (_filePath is null) return false;
        _doc.FileActionOccurred += OnFileActionOccurred;
        RebuildTrackedFromService();
        if (_tracked.Count > 0) RenderHunks();
        return true;
    }

    // VS just replaced the buffer with fresh disk content (e.g. after the
    // host's tool wrote the file). Tracking spans built against the old
    // snapshot are now stale — clear local invalidation flags and rebuild.
    void OnFileActionOccurred(object? sender, TextDocumentFileActionEventArgs e)
    {
        if ((e.FileActionType & FileActionTypes.ContentLoadedFromDisk) == 0) return;
        foreach (var t in _tracked) t.LocallyInvalidated = false;
        RebuildTrackedFromService();
        RenderHunks();
    }

    void OnHunksChanged(string changedPath)
    {
        if (!EnsureFilePath()) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        RebuildTrackedFromService();
        Debug.WriteLine($"[ChatRelay.editor] Hunks for {_filePath}: {_tracked.Count} hunk(s)");
        RenderHunks();
    }

    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        // Layout fires on every reformat / scroll / edit — a good moment to
        // retry file-path resolution if it failed earlier.
        if (!EnsureFilePath()) return;
        if (_tracked.Count == 0) return;
        RenderHunks();
    }

    // Watch buffer edits so a user-driven undo / wipe of the model's new
    // lines hides the hunk locally before the next host snapshot.
    // External-file reloads (VS replacing the whole buffer when the host
    // wrote the file) are skipped — those aren't user typing, and the
    // tracking span temporarily collapses, which would falsely flag every
    // hunk obsolete. FileActionOccurred handles those instead.
    void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        if (!EnsureFilePath()) return;
        if (_tracked.Count == 0) return;
        if (IsFullBufferReplace(e)) return;

        var snapshot = e.After;
        foreach (var t in _tracked)
        {
            if (t.LocallyInvalidated) continue;

            SnapshotSpan current;
            try { current = t.Span.GetSpan(snapshot); }
            catch { continue; }

            bool intersects = false;
            foreach (var change in e.Changes)
            {
                if (change.NewSpan.IntersectsWith(current.Span))
                {
                    intersects = true;
                    break;
                }
            }
            if (!intersects) continue;

            if (IsHunkObsolete(t, current))
                t.LocallyInvalidated = true;
        }
    }

    // One change covering the entire prior buffer: VS reloading from disk.
    static bool IsFullBufferReplace(TextContentChangedEventArgs e) =>
        e.Changes.Count == 1 && e.Changes[0].OldSpan.Length == e.Before.Length;

    /// <summary>
    /// True iff none of the model's new lines for this open hunk are
    /// still present in its tracked span. Triggered by ctrl+Z fully
    /// undoing the model's edit, by the user selecting the inserted
    /// code and deleting it, or by any other edit that wipes the
    /// model's content out of that region.
    ///
    /// <para>
    /// Pure-deletion hunks (CurrentLines empty) skip — there's nothing
    /// to check; their tracked span is zero-width at the join-point.
    /// Pure-insertion hunks where the span has collapsed to zero
    /// length are obviously obsolete (the inserted lines are gone).
    /// </para>
    /// </summary>
    static bool IsHunkObsolete(TrackedHunk t, SnapshotSpan current)
    {
        if (t.Info.CurrentLines.Count == 0) return false;
        if (current.Length == 0) return true;
        string spanText;
        try { spanText = current.GetText(); }
        catch { return false; }
        // Exact line match — substring would false-positive on common
        // tokens. Splits on \r\n / \n / \r so we don't mis-match across
        // newline styles.
        var lines = spanText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var lineSet = new HashSet<string>(lines, StringComparer.Ordinal);
        foreach (var ml in t.Info.CurrentLines)
        {
            if (lineSet.Contains(ml)) return false;
        }
        return true;
    }

    void RebuildTrackedFromService()
    {
        _tracked.Clear();
        var hunks = _service.GetHunks(_filePath!);
        if (hunks.Count == 0) return;

        var snapshot = _view.TextSnapshot;
        foreach (var h in hunks)
        {
            var span = TryBuildTrackingSpan(h, snapshot);
            if (span is null) continue;
            _tracked.Add(new TrackedHunk { Info = h, Span = span! });
        }
    }

    // EdgeInclusive so user typing at the hunk boundary grows the
    // tracked region. Pure-deletion hunks (CurrentCount=0) anchor on
    // the join-line with full line width so CreatePopupAgent has enough
    // geometry to place the buttons; the highlight path skips them so
    // the line-wide span doesn't paint blue over the surviving line.
    static ITrackingSpan? TryBuildTrackingSpan(HunkInfo h, ITextSnapshot snapshot)
    {
        try
        {
            if (h.CurrentCount > 0)
            {
                // Buffer hasn't caught up to the host's line numbering yet.
                // Defer; FileActionOccurred(ContentLoadedFromDisk) retries.
                if (h.CurrentStart < 0
                    || h.CurrentStart + h.CurrentCount > snapshot.LineCount) return null;
                var first = snapshot.GetLineFromLineNumber(h.CurrentStart);
                var last = snapshot.GetLineFromLineNumber(h.CurrentStart + h.CurrentCount - 1);
                return snapshot.CreateTrackingSpan(
                    new SnapshotSpan(first.Start, last.End),
                    SpanTrackingMode.EdgeInclusive);
            }
            int anchorLine = Math.Max(0, Math.Min(h.CurrentStart, snapshot.LineCount - 1));
            var line = snapshot.GetLineFromLineNumber(anchorLine);
            return snapshot.CreateTrackingSpan(line.Extent, SpanTrackingMode.EdgeExclusive);
        }
        catch
        {
            return null;
        }
    }

    void RenderHunks()
    {
        if (_layer is null) return;
        _layer.RemoveAllAdornments();
        _overlay?.RemoveAllAdornments();

        var snapshot = _view.TextSnapshot;
        foreach (var t in _tracked)
        {
            if (t.LocallyInvalidated) continue;

            try
            {
                var live = t.Span.GetSpan(snapshot);
                RenderOpenHunkHighlight(t, live, snapshot);
                RenderHunkButtons(t, live);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRelay.editor] Render hunk failed: {ex.Message}");
            }
        }
    }

    // Anchors at the top of the hunk's first VISIBLE line so buttons
    // stay reachable as the user scrolls through hunks taller than the
    // viewport. Right-aligned to the viewport edge.
    void RenderHunkButtons(TrackedHunk t, SnapshotSpan span)
    {
        if (_overlay is null) return;
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;
        var firstView = formatted[0];

        var row = BuildButtonRow(t.Info);
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = row.DesiredSize;

        Canvas.SetLeft(row, _view.ViewportLeft + _view.ViewportWidth - size.Width - 8);
        Canvas.SetTop(row, firstView.TextTop);
        _overlay.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(t.Info, "buttons"), row, null);
    }

    // Highlight rect only — buttons render separately in RenderHunkButtons.
    void RenderOpenHunkHighlight(TrackedHunk t, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;
        // Pure-deletion hunks have no current lines to highlight — the red
        // strip from HunkRemovedLinesTagger is what represents them. Their
        // tracking span is line-wide (so the popup can position) but we
        // must NOT paint blue over the surviving line below the deletion.
        if (t.Info.CurrentCount == 0) return;
        if (span.Length <= 0) return;

        // Intersect-the-span keeps the highlight visible when the
        // hunk is partially scrolled off-screen.
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;
        var firstView = formatted[0];
        var lastView = formatted[formatted.Count - 1];

        // TextTop / TextBottom — Top includes any reserved top-space VS
        // allocated above the line for HunkRemovedLinesTagger's
        // InterLineAdornmentTag, which would z-fight the red strip.
        var rect = new Rectangle
        {
            Fill = HighlightFill,
            IsHitTestVisible = false,
            Width = _view.ViewportWidth,
            Height = lastView.TextBottom - firstView.TextTop,
        };
        Canvas.SetLeft(rect, _view.ViewportLeft);
        Canvas.SetTop(rect, firstView.TextTop);
        _layer.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(t.Info, "highlight"), rect, null);
    }

    // Square 24×24 buttons; 6px gap between accept and reject.
    const double SmallButtonWidth = 24;
    const double SmallButtonHeight = 24;
    const double SmallButtonGap = 6;

    UIElement BuildButtonRow(HunkInfo h)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var accept = BuildIconButton("✓", primary: true);
        accept.ToolTip = "Accept this change";
        accept.Click += async (_, _) => await OnAcceptClicked(h);
        row.Children.Add(accept);

        var reject = BuildIconButton("↶", primary: false);
        reject.ToolTip = "Revert this change";
        reject.Margin = new Thickness(SmallButtonGap, 0, 0, 0);
        reject.Click += async (_, _) => await OnRejectClicked(h);
        row.Children.Add(reject);

        return row;
    }

    async System.Threading.Tasks.Task OnAcceptClicked(HunkInfo h)
    {
        var sid = _service.CurrentSessionId;
        var op = _service.AcceptHunkAsync;
        if (sid is null || op is null || _filePath is null) return;
        TrySaveBuffer();
        try { await op(sid, _filePath, h.BaselineStart, h.BaselineCount); }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] AcceptHunk failed: {ex.Message}"); }
    }

    async System.Threading.Tasks.Task OnRejectClicked(HunkInfo h)
    {
        var sid = _service.CurrentSessionId;
        var op = _service.RejectHunkAsync;
        if (sid is null || op is null || _filePath is null) return;
        TrySaveBuffer();
        try { await op(sid, _filePath, h.BaselineStart, h.BaselineCount); }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] RejectHunk failed: {ex.Message}"); }
    }

    /// <summary>
    /// Persists the buffer to disk before accept/reject runs on the host.
    /// Two reasons:
    ///   • Without this, the host writes <c>Baseline</c> back over a file
    ///     the editor still has dirty in-memory; VS prompts the user with
    ///     "the file was modified externally — reload?" — annoying every
    ///     time they hit ↶.
    ///   • Saves the user's in-buffer typing into <c>LastApplied</c> so
    ///     reject covers their edits along with the model's, and a
    ///     subsequent redo restores the user's lines too.
    /// No-op if the buffer isn't dirty or there's no <c>ITextDocument</c>
    /// (transient buffers, peek windows, etc.).
    /// </summary>
    void TrySaveBuffer()
    {
        try
        {
            if (_view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc)
                && doc.IsDirty)
            {
                doc.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] save-before-action failed: {ex.Message}");
        }
    }

    // Two cached ControlTemplates — primary (turquoise) for accept, neutral
    // for reject. Building the FrameworkElementFactory + Triggers on every
    // RenderHunks pass was a non-trivial WPF allocation; share once.
    static readonly Brush PrimaryBorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xB6, 0xAB)));
    static readonly ControlTemplate PrimaryTemplate = BuildButtonTemplate(primary: true);
    static readonly ControlTemplate SecondaryTemplate = BuildButtonTemplate(primary: false);

    static ControlTemplate BuildButtonTemplate(bool primary)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding(nameof(Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            primary ? AccentBrushHover : (Brush?)Application.Current.TryFindResource(EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey) ?? Brushes.LightGray,
            "Bd"));
        var pressTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            primary ? AccentBrushPressed : (Brush?)Application.Current.TryFindResource(EnvironmentColors.CommandBarSelectedBrushKey) ?? Brushes.Gray,
            "Bd"));
        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(pressTrigger);
        template.Seal();
        return template;
    }

    // 24×24 icon-only buttons — accept gets the brand turquoise, reject
    // gets a theme-bound neutral surface. Custom ControlTemplate so WPF's
    // default hover/pressed gradients don't override our colors.
    static Button BuildIconButton(string glyph, bool primary)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = SmallButtonWidth,
            Height = SmallButtonHeight,
            FontSize = 13,
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = primary ? PrimaryTemplate : SecondaryTemplate,
        };

        if (primary)
        {
            btn.Background = AccentBrush;
            btn.BorderBrush = PrimaryBorderBrush;
            btn.Foreground = Brushes.White;
        }
        else
        {
            btn.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            btn.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
            btn.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }
        return btn;
    }

    // Identifies an adornment so the layer can match it on removal.
    static object AdornmentTagFor(HunkInfo h, string role) =>
        $"{h.BaselineStart}:{h.BaselineCount}:{role}";

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _view.Closed -= OnViewClosed;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.TextBuffer.Changed -= OnBufferChanged;
        if (_doc is not null) _doc.FileActionOccurred -= OnFileActionOccurred;
    }
}

