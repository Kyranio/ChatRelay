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
/// Per-editor-view glue between <see cref="EditorChangesService"/> and the
/// <c>ChatRelayHunks</c> adornment layer. One instance lives for the lifetime
/// of one <see cref="IWpfTextView"/>:
/// <list type="bullet">
///   <item>Resolves the view's underlying file path via the buffer's
///   <see cref="ITextDocument"/> property.</item>
///   <item>Subscribes to <see cref="EditorChangesService.HunksChanged"/>
///   filtered to its file plus <see cref="IWpfTextView.LayoutChanged"/>
///   (so adornments follow scroll / resize / edits).</item>
///   <item>Renders open hunks as: a turquoise translucent highlight behind
///   the new lines plus a panel below containing a collapsible-by-default
///   "{N} removed lines" expander and a horizontal accept/reject button
///   row. Accepted hunks are intentionally skipped here — Phase 4.3c will
///   replace them with a thin marker bar.</item>
/// </list>
/// </summary>
sealed class HunkAdornmentManager
{
    // Brand turquoise — matches the chat-side ClaudeButtonStyle's accent.
    static readonly Color AccentColor = Color.FromRgb(0x40, 0xE0, 0xD0);
    static readonly Brush AccentBrush = Frozen(new SolidColorBrush(AccentColor));
    static readonly Brush AccentBrushHover = Frozen(new SolidColorBrush(Color.FromRgb(0x5F, 0xE6, 0xD8)));
    static readonly Brush AccentBrushPressed = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x88, 0x81)));
    // Translucent overlay for the new-line highlight; alpha picked to be
    // visible without obscuring text or syntax-highlight colors.
    static readonly Brush HighlightFill = Frozen(new SolidColorBrush(Color.FromArgb(0x35, 0x40, 0xE0, 0xD0)));

    readonly IWpfTextView _view;
    readonly string? _filePath;
    readonly EditorChangesService _service;
    // Background layer (below Text): turquoise highlight + accepted marker
    // bar. Overlay layer (above Text): accept/reject buttons. Splitting
    // the two means the editor's source code text stays the topmost
    // *visible* content for the highlight, while the interactive
    // controls anchored below the hunk's lines aren't obscured by the
    // editor's own text painted in those rows.
    //
    // The "removed lines" inline red block is NOT rendered here — it's
    // produced by HunkRemovedLinesTagger via VS's InterLineAdornmentTag
    // primitive, which handles space reservation and adornment
    // positioning automatically. That removed the manual
    // LineTransform/Canvas dance that used to race against the format
    // pass and produce overlapping blocks until the user typed.
    readonly IAdornmentLayer? _layer;
    readonly IAdornmentLayer? _overlay;

    // Last hunk list we received from the service, paired with an
    // ITrackingSpan over each hunk's current line range. The tracking
    // span lets the highlight + buttons follow the user's typing live —
    // VS auto-translates the span across buffer edits, so the next
    // render reads the up-to-date coords without waiting for a save.
    //
    // LocallyInvalidated covers the "drop accepted marker on first
    // touch" rule: when the user types within an accepted hunk's
    // tracked range we set the bit, hide the marker immediately, and
    // fire an invalidate RPC so the next host snapshot agrees.
    readonly List<TrackedHunk> _tracked = new();

    // Plain mutable settable properties — net48 doesn't have init/required
    // without polyfill attributes.
    sealed class TrackedHunk
    {
        public HunkInfo Info { get; set; } = null!;
        public ITrackingSpan Span { get; set; } = null!;
        public bool LocallyInvalidated { get; set; }
    }

    public HunkAdornmentManager(IWpfTextView view)
    {
        _view = view;
        _filePath = ResolveFilePath(view);
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

        if (_filePath is null) return;

        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;
        _view.LayoutChanged += OnLayoutChanged;
        _view.TextBuffer.Changed += OnBufferChanged;

        // Pick up any hunks already known (the snapshot may have arrived
        // before the editor view opened).
        RebuildTrackedFromService();
        if (_tracked.Count > 0) RenderHunks();
    }

    void OnHunksChanged(string changedPath)
    {
        if (_filePath is null) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        RebuildTrackedFromService();
        Debug.WriteLine($"[ChatRelay.editor] Hunks for {_filePath}: {_tracked.Count} hunk(s)");
        RenderHunks();
    }

    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        // Re-render on every layout change so adornments follow scroll,
        // resize, and text edits. Buffer edits also reach us here; the
        // tracking spans built in RebuildTrackedFromService auto-translate
        // across snapshots, so the render at this point uses live coords.
        if (_tracked.Count == 0) return;
        RenderHunks();
    }

    void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        // The watcher / host snapshot path can't see in-buffer edits
        // until the user saves. Three concerns:
        //   • Accepted hunks intersected by typing → drop the marker
        //     locally and tell the host (folds the hunk into Baseline).
        //   • Open hunks whose model new-lines have been undone /
        //     backspaced out of the buffer → hide locally; the host
        //     catches up on next save when LastApplied catches up to
        //     disk and the diff naturally shrinks.
        //   • Open hunks that simply grew or shrank due to typing
        //     inside the hunk → no action; tracking spans already
        //     follow.
        if (_tracked.Count == 0) return;

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
                if (change.NewSpan.OverlapsWith(current.Span)
                    || change.NewSpan.IntersectsWith(current.Span))
                {
                    intersects = true;
                    break;
                }
            }
            if (!intersects) continue;

            // Open vs accepted have different invalidation rules.
            if (string.Equals(t.Info.State, "accepted", StringComparison.Ordinal))
            {
                // Accepted: any touch drops the marker. Host RPC folds
                // the hunk into Baseline.
                t.LocallyInvalidated = true;
                FireInvalidateAcceptedHunk(t.Info);
                continue;
            }

            // Open: a touch is fine (typing inside the hunk is the
            // normal case — span auto-grows). But if the touch removed
            // ALL of the model's new lines from the tracked span — e.g.
            // ctrl+Z, or the user selected the new code and pressed
            // backspace — the hunk is obsolete and should disappear
            // visually. The host's view will catch up on next save.
            if (IsHunkObsolete(t, current))
                t.LocallyInvalidated = true;
        }
    }

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

    void FireInvalidateAcceptedHunk(HunkInfo h)
    {
        var sid = _service.CurrentSessionId;
        var op = _service.InvalidateAcceptedHunkAsync;
        if (sid is null || op is null || _filePath is null) return;
        // Fire-and-forget — host catches up on its next snapshot. Wrap
        // in a Task so JsonRpc exceptions don't bubble onto the UI thread.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await op(sid, _filePath, h.BaselineStart, h.BaselineCount); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRelay.editor] InvalidateAcceptedHunk failed: {ex.Message}");
            }
        });
    }

    void RebuildTrackedFromService()
    {
        _tracked.Clear();
        if (_filePath is null) return;
        var hunks = _service.GetHunks(_filePath);
        if (hunks.Count == 0) return;

        var snapshot = _view.TextSnapshot;
        foreach (var h in hunks)
        {
            var span = TryBuildTrackingSpan(h, snapshot);
            if (span is null) continue;
            _tracked.Add(new TrackedHunk { Info = h, Span = span! });
        }
    }

    // Builds an EdgeInclusive tracking span covering the hunk's current
    // line range. EdgeInclusive matters: the user inserting a newline at
    // the boundary of the hunk should grow the tracked region (so the
    // highlight follows). Pure-deletion hunks (CurrentCount=0) get a
    // zero-width span at the join-point so we can still anchor a UI to
    // them, but they aren't candidates for "intersected by typing"
    // since there's nothing to type inside.
    static ITrackingSpan? TryBuildTrackingSpan(HunkInfo h, ITextSnapshot snapshot)
    {
        try
        {
            if (h.CurrentCount > 0
                && h.CurrentStart >= 0
                && h.CurrentStart + h.CurrentCount <= snapshot.LineCount)
            {
                var first = snapshot.GetLineFromLineNumber(h.CurrentStart);
                var last = snapshot.GetLineFromLineNumber(h.CurrentStart + h.CurrentCount - 1);
                return snapshot.CreateTrackingSpan(
                    new SnapshotSpan(first.Start, last.End),
                    SpanTrackingMode.EdgeInclusive);
            }
            // Pure deletion — zero-width anchor at the join-point.
            int anchorLine = Math.Max(0, Math.Min(h.CurrentStart, snapshot.LineCount - 1));
            var line = snapshot.GetLineFromLineNumber(anchorLine);
            return snapshot.CreateTrackingSpan(new SnapshotSpan(line.Start, 0), SpanTrackingMode.EdgeExclusive);
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
            // Skip accepted hunks the user has typed into. The marker is
            // suppressed locally until the next snapshot (which the host
            // RPC will re-classify as open).
            if (t.LocallyInvalidated) continue;

            try
            {
                var live = t.Span.GetSpan(snapshot);
                if (string.Equals(t.Info.State, "accepted", StringComparison.Ordinal))
                    RenderAcceptedHunk(t.Info, live, snapshot);
                else
                    RenderOpenHunk(t.Info, live, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRelay.editor] Render hunk failed: {ex.Message}");
            }
        }
    }

    void RenderAcceptedHunk(HunkInfo h, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;
        // Accepted hunk marker — set in stone under the simplified model:
        //   - thin turquoise vertical bar in the left margin of the lines
        //     the model produced (positioned via the live tracking span,
        //     so it follows the user's edits around it)
        //   - tooltip "Edited by {Model}" for provenance
        // No inline reject button: an accepted hunk is permanent. The user
        // reverts via the editor's own undo stack (ctrl+Z) — that's the
        // story we're committing to so users can never bring a removed
        // hunk back into the change list themselves.
        if (span.Length <= 0) return;

        // GetTextViewLinesIntersectingSpan keeps the marker visible when
        // the user has scrolled so part of the hunk is off-screen — we
        // anchor to the visible portion instead of bailing.
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;
        var firstView = formatted[0];
        var lastView = formatted[formatted.Count - 1];

        var modelLabel = string.IsNullOrEmpty(h.Model)
            ? "Edited by the model"
            : $"Edited by {h.Model}";

        // TextTop / TextBottom — see RenderOpenHunk for why. Using Top
        // would stretch the marker bar into any reserved gap above the
        // first line and overlap an InterLineAdornment-provided gap.
        var bar = new Rectangle
        {
            Fill = AccentBrush,
            Width = 3,
            Height = lastView.TextBottom - firstView.TextTop,
            ToolTip = modelLabel,
            Cursor = System.Windows.Input.Cursors.Help,
        };
        Canvas.SetLeft(bar, _view.ViewportLeft + 1);
        Canvas.SetTop(bar, firstView.TextTop);
        _layer.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(h, "accepted-bar"), bar, null);
    }

    void RenderOpenHunk(HunkInfo h, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;

        // ---- 1. Highlight rectangle behind the new lines (when any) -----
        double belowY;       // Y to position the expander + buttons row

        if (span.Length > 0)
        {
            // GetTextViewLinesIntersectingSpan returns only the formatted
            // lines that overlap the hunk's span — including the case
            // where the hunk extends past the viewport in either
            // direction. We then anchor the highlight + buttons to the
            // visible portion. Using GetTextViewLineContainingBufferPosition
            // returns null when the exact buffer position isn't in any
            // formatted line (off-screen by even one pixel), which used
            // to make the entire hunk's UI disappear during scrolling.
            var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
            if (formatted is null || formatted.Count == 0) return;

            var firstView = formatted[0];
            var lastView = formatted[formatted.Count - 1];

            // TextTop / TextBottom (the actual glyph band) rather than
            // Top / Bottom (which include any reserved top/bottom-space —
            // e.g. the gap VS reserves above this line for the
            // HunkRemovedLinesTagger's InterLineAdornmentTag). Painting
            // from Top would stretch the highlight up into the reserved
            // gap and z-fight with the red removed-lines block, hiding
            // the blue completely.
            var rect = new Rectangle
            {
                Fill = HighlightFill,
                IsHitTestVisible = false,    // text below stays interactive
                Width = _view.ViewportWidth,
                Height = lastView.TextBottom - firstView.TextTop,
            };
            Canvas.SetLeft(rect, _view.ViewportLeft);
            Canvas.SetTop(rect, firstView.TextTop);
            _layer.AddAdornment(
                AdornmentPositioningBehavior.OwnerControlled,
                span, AdornmentTagFor(h, "highlight"), rect, null);
            // Buttons go just below the last visible line's text — if the
            // hunk's actual last line is off-screen below the viewport,
            // lastView is the last visible line and buttons end up just
            // above the bottom edge instead of clipping out entirely.
            belowY = lastView.TextBottom;
        }
        else
        {
            // Pure deletion: anchor the buttons just below the join-point
            // line's text. The red block sits in the reserved top-space
            // ABOVE this line; the buttons go BELOW so they don't collide.
            // Use a span over the entire join-point line so partial-
            // viewport scrolling still surfaces the buttons.
            int lineNum = snapshot.GetLineNumberFromPosition(span.Start.Position);
            if (lineNum >= snapshot.LineCount) return;
            var line = snapshot.GetLineFromLineNumber(lineNum);
            var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(
                new SnapshotSpan(line.Start, line.LengthIncludingLineBreak));
            if (formatted is null || formatted.Count == 0) return;
            belowY = formatted[0].TextBottom;
        }

        var anchorSpan = new SnapshotSpan(span.Start, 0);

        // Inline removed-lines block is rendered by HunkRemovedLinesTagger
        // via VS's InterLineAdornmentTag — VS reserves the gap and
        // positions the adornment automatically. Nothing to paint here.

        // Buttons below the highlight, anchored at viewport's left.
        var buttons = BuildButtonRow(h);
        if (buttons is FrameworkElement fe)
            fe.VerticalAlignment = VerticalAlignment.Top;

        Canvas.SetLeft(buttons, _view.ViewportLeft + 4);
        Canvas.SetTop(buttons, belowY + 2);
        // Overlay layer (above Text) so the editor's own text in this
        // row doesn't paint over the buttons.
        (_overlay ?? _layer).AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            anchorSpan, AdornmentTagFor(h, "buttons"), buttons, null);
    }

    // Square 24×24 buttons sized to match the Expander header chrome
    // (FontSize 11 + Padding (6,2,6,2) + ~border ≈ 24px tall) so the
    // accept/reject row lines up perfectly with the "{N} removed lines"
    // header. 6px gap between the two buttons.
    const double SmallButtonWidth = 24;
    const double SmallButtonHeight = 24;
    const double SmallButtonGap = 6;
    const double OpenButtonRowWidth = SmallButtonWidth * 2 + SmallButtonGap;

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

    // 24×24 icon-only buttons — accept gets the brand turquoise, reject
    // gets a theme-bound neutral surface. Squared off to line up with the
    // Expander header next to them. Custom ControlTemplate so WPF's
    // default hover/pressed gradients don't override our colors.
    static Button BuildIconButton(string glyph, bool primary)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = SmallButtonWidth,
            Height = SmallButtonHeight,
            FontSize = 13,    // bumped from 11 to fill the larger square
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

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
            primary ? AccentBrushHover : (Brush)Application.Current.TryFindResource(EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey) ?? Brushes.LightGray,
            "Bd"));
        var pressTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            primary ? AccentBrushPressed : (Brush)Application.Current.TryFindResource(EnvironmentColors.CommandBarSelectedBrushKey) ?? Brushes.Gray,
            "Bd"));
        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(pressTrigger);

        btn.Template = template;

        if (primary)
        {
            btn.Background = AccentBrush;
            btn.BorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xB6, 0xAB)));
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

    // Tag identifies an adornment so it can be matched/removed later.
    // Format: <baseline-start>:<baseline-count>:<role>. Phase 4.4 may use
    // these to incrementally update only the affected hunks.
    static object AdornmentTagFor(HunkInfo h, string role) =>
        $"{h.BaselineStart}:{h.BaselineCount}:{role}";

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _view.Closed -= OnViewClosed;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.TextBuffer.Changed -= OnBufferChanged;
    }

    static string? ResolveFilePath(IWpfTextView view)
    {
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }
}

