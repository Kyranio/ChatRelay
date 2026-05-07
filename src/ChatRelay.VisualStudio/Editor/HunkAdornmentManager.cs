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

/// <summary>Per-view renderer for the turquoise highlight + accept/reject button row on each open hunk.</summary>
sealed class HunkAdornmentManager
{
    static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0xD0)));
    static readonly Brush AccentBrushHover = Frozen(new SolidColorBrush(Color.FromRgb(0x5F, 0xE6, 0xD8)));
    static readonly Brush AccentBrushPressed = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x88, 0x81)));
    static readonly Brush HighlightFill = Frozen(new SolidColorBrush(Color.FromArgb(0x35, 0x40, 0xE0, 0xD0)));
    static readonly Brush PrimaryBorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xB6, 0xAB)));
    static readonly ControlTemplate PrimaryTemplate = BuildButtonTemplate(primary: true);
    static readonly ControlTemplate SecondaryTemplate = BuildButtonTemplate(primary: false);

    const double ButtonSize = 24;
    const double ButtonGap = 6;
    const double StickyTopMargin = 6;

    readonly IWpfTextView _view;
    readonly EditorChangesService _service;
    readonly IAdornmentLayer? _layer;
    readonly IAdornmentLayer? _overlay;
    readonly List<TrackedHunk> _tracked = new();
    // ITextDocument can attach AFTER IWpfTextViewCreationListener fires; resolve lazily from event handlers.
    string? _filePath;
    ITextDocument? _doc;

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
        }

        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;
        _view.LayoutChanged += OnLayoutChanged;
        _view.TextBuffer.Changed += OnBufferChanged;
        EnsureFilePath();
    }

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

    // VS just replaced the buffer with disk content (host wrote the file): tracking spans are stale, rebuild.
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
        RenderHunks();
    }

    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        if (!EnsureFilePath()) return;
        if (_tracked.Count == 0) return;
        RenderHunks();
    }

    // Local-undo detector: hide a hunk visually when the user wipes the model's content from the tracked span.
    // Skip on full-buffer-replace events (VS reloading from disk) — FileActionOccurred handles those.
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
                if (change.NewSpan.IntersectsWith(current.Span)) { intersects = true; break; }
            if (!intersects) continue;

            if (IsHunkObsolete(t, current)) t.LocallyInvalidated = true;
        }
    }

    static bool IsFullBufferReplace(TextContentChangedEventArgs e) =>
        e.Changes.Count == 1 && e.Changes[0].OldSpan.Length == e.Before.Length;

    // True iff none of the model's new lines for this hunk are still present in its tracked span.
    static bool IsHunkObsolete(TrackedHunk t, SnapshotSpan current)
    {
        if (t.Info.CurrentLines.Count == 0) return false;
        if (current.Length == 0) return true;
        string spanText;
        try { spanText = current.GetText(); }
        catch { return false; }
        var lines = spanText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var lineSet = new HashSet<string>(lines, StringComparer.Ordinal);
        foreach (var ml in t.Info.CurrentLines)
            if (lineSet.Contains(ml)) return false;
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
            if (span is not null) _tracked.Add(new TrackedHunk { Info = h, Span = span });
        }
    }

    // EdgeInclusive so user typing at the boundary grows the region. Pure deletions anchor on the join-line full-width.
    // Returns null if the buffer hasn't caught up to the host's line numbering yet — FileActionOccurred retries.
    static ITrackingSpan? TryBuildTrackingSpan(HunkInfo h, ITextSnapshot snapshot)
    {
        try
        {
            if (h.CurrentCount > 0)
            {
                if (h.CurrentStart < 0 || h.CurrentStart + h.CurrentCount > snapshot.LineCount) return null;
                var first = snapshot.GetLineFromLineNumber(h.CurrentStart);
                var last = snapshot.GetLineFromLineNumber(h.CurrentStart + h.CurrentCount - 1);
                return snapshot.CreateTrackingSpan(new SnapshotSpan(first.Start, last.End), SpanTrackingMode.EdgeInclusive);
            }
            int anchorLine = Math.Max(0, Math.Min(h.CurrentStart, snapshot.LineCount - 1));
            return snapshot.CreateTrackingSpan(snapshot.GetLineFromLineNumber(anchorLine).Extent, SpanTrackingMode.EdgeExclusive);
        }
        catch { return null; }
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
                RenderHighlight(t, live);
                RenderButtons(t, live);
            }
            catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] Render hunk failed: {ex.Message}"); }
        }
    }

    // Pure-deletion hunks (CurrentCount=0) skip — the red strip from HunkRemovedLinesTagger represents them.
    // Use TextTop/TextBottom so the highlight doesn't extend into reserved gap-space above the line.
    void RenderHighlight(TrackedHunk t, SnapshotSpan span)
    {
        if (_layer is null || t.Info.CurrentCount == 0 || span.Length <= 0) return;
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;
        var firstView = formatted[0];
        var lastView = formatted[formatted.Count - 1];

        var rect = new Rectangle
        {
            Fill = HighlightFill,
            IsHitTestVisible = false,
            Width = _view.ViewportWidth,
            Height = lastView.TextBottom - firstView.TextTop,
        };
        Canvas.SetLeft(rect, _view.ViewportLeft);
        Canvas.SetTop(rect, firstView.TextTop);
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, span, AdornmentTagFor(t.Info, "highlight"), rect, null);
    }

    // CSS-sticky positioning: anchored at the hunk's visual top (red strip if any, else blue), clamped to viewport top
    // with a margin as the hunk scrolls past, then released downward when the hunk's bottom approaches.
    void RenderButtons(TrackedHunk t, SnapshotSpan span)
    {
        if (_overlay is null) return;
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;

        var row = BuildButtonRow(t.Info);
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rowH = row.DesiredSize.Height;

        var firstView = formatted[0];
        bool hasStrip = t.Info.BaselineLines.Count > 0;
        // .Top includes any reserved gap above the line (= top of the red strip when there is one).
        double hunkTopY = hasStrip ? firstView.Top : firstView.TextTop;

        double hunkBottomY;
        if (t.Info.CurrentCount == 0)
        {
            // Pure deletion: the visible hunk is just the strip; it ends where the surviving line's text begins.
            hunkBottomY = firstView.TextTop;
        }
        else
        {
            // For multi-line hunks the last visible line may be earlier than the hunk's actual last line; use it
            // anyway as the bottom-bound only when we know we have it, otherwise treat bottom as off-screen below.
            int lastHunkLine = t.Info.CurrentStart + t.Info.CurrentCount - 1;
            var lastView = formatted[formatted.Count - 1];
            int lastViewLine = span.Snapshot.GetLineNumberFromPosition(lastView.Start.Position);
            hunkBottomY = lastViewLine >= lastHunkLine ? lastView.TextBottom : double.PositiveInfinity;
        }

        double stickyTop = _view.ViewportTop + StickyTopMargin;
        double targetTop = hunkTopY >= stickyTop ? hunkTopY : Math.Min(stickyTop, hunkBottomY - rowH);
        if (targetTop + rowH < _view.ViewportTop) return;

        Canvas.SetLeft(row, _view.ViewportLeft + _view.ViewportWidth - row.DesiredSize.Width - 8);
        Canvas.SetTop(row, targetTop);
        _overlay.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, span, AdornmentTagFor(t.Info, "buttons"), row, null);
    }

    StackPanel BuildButtonRow(HunkInfo h)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var accept = BuildIconButton("✓", primary: true);
        accept.ToolTip = "Accept this change";
        accept.Click += async (_, _) => await OnAcceptClicked(h);
        row.Children.Add(accept);

        var reject = BuildIconButton("↶", primary: false);
        reject.ToolTip = "Revert this change";
        reject.Margin = new Thickness(ButtonGap, 0, 0, 0);
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

    // Without this, the host writes Baseline over a dirty buffer and VS prompts "modified externally — reload?" on every reject.
    void TrySaveBuffer()
    {
        try
        {
            if (_view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc) && doc.IsDirty)
                doc.Save();
        }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] save-before-action failed: {ex.Message}"); }
    }

    static ControlTemplate BuildButtonTemplate(bool primary)
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bd" };
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
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            primary ? AccentBrushHover : (Brush?)Application.Current.TryFindResource(EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey) ?? Brushes.LightGray, "Bd"));
        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty,
            primary ? AccentBrushPressed : (Brush?)Application.Current.TryFindResource(EnvironmentColors.CommandBarSelectedBrushKey) ?? Brushes.Gray, "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(press);
        template.Seal();
        return template;
    }

    static Button BuildIconButton(string glyph, bool primary)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = ButtonSize,
            Height = ButtonSize,
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

    static object AdornmentTagFor(HunkInfo h, string role) => $"{h.BaselineStart}:{h.BaselineCount}:{role}";

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
