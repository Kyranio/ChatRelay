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
    // bar. Overlay layer (above Text): ghost expander + accept/reject buttons.
    // Splitting the two means the editor's source code text stays the topmost
    // *visible* content for the highlight, while the interactive controls
    // anchored below the hunk's lines aren't obscured by the editor's own
    // text painted in those rows.
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
        // until the user saves. We do two things on every change:
        //   • Detect intersections with accepted-hunk tracked spans →
        //     drop those markers locally and tell the host so the next
        //     snapshot agrees.
        //   • Don't bother re-rendering open hunks here; the tracking
        //     spans auto-grow and OnLayoutChanged already fires for
        //     buffer edits, so the next render uses the live coords.
        if (_tracked.Count == 0) return;

        var snapshot = e.After;
        foreach (var t in _tracked)
        {
            if (t.LocallyInvalidated) continue;
            if (!string.Equals(t.Info.State, "accepted", StringComparison.Ordinal)) continue;

            var current = t.Span.GetSpan(snapshot);
            // Any change within the tracked region invalidates the marker.
            // ITextChange.NewSpan is in the after-snapshot — direct compare.
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

            t.LocallyInvalidated = true;
            FireInvalidateAcceptedHunk(t.Info);
        }
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
        // Accepted hunk marker:
        //   - thin turquoise vertical bar in the left margin of the lines
        //     the model produced (positioned via the live tracking span,
        //     so it follows the user's edits around it)
        //   - tooltip "Edited by {Model}" — model resolved from the hunk's
        //     Model field (carried on the wire by Phase 4.3a)
        //   - reject (↶) button below the region so the user can still
        //     change their mind from inside the editor
        if (span.Length <= 0) return;

        var firstView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
        // span.End-1 keeps us on the last actual line, not the next one.
        var lastPos = span.End > span.Start ? span.End - 1 : span.End;
        var lastView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(lastPos);
        if (firstView is null || lastView is null) return;

        var modelLabel = string.IsNullOrEmpty(h.Model)
            ? "Edited by the model"
            : $"Edited by {h.Model}";

        var bar = new Rectangle
        {
            Fill = AccentBrush,
            Width = 3,
            Height = lastView.Bottom - firstView.Top,
            ToolTip = modelLabel,
            Cursor = System.Windows.Input.Cursors.Help,
        };
        Canvas.SetLeft(bar, _view.ViewportLeft + 1);
        Canvas.SetTop(bar, firstView.Top);
        _layer.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(h, "accepted-bar"), bar, null);

        // Reject button below the region — same compact secondary style
        // as open hunks, anchored at the viewport's left edge to mirror the
        // open-hunk layout. Accept is intentionally absent (already accepted).
        var reject = BuildIconButton("↶", primary: false);
        reject.ToolTip = $"Revert this change ({modelLabel})";
        reject.Click += async (_, _) => await OnRejectClicked(h);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(reject);
        Canvas.SetLeft(row, _view.ViewportLeft + 4);
        Canvas.SetTop(row, lastView.Bottom + 2);
        // Overlay layer — sits above editor Text so the button isn't
        // obscured by the source code drawn in this row.
        (_overlay ?? _layer).AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(h, "accepted-reject"), row, null);
    }

    void RenderOpenHunk(HunkInfo h, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;

        // ---- 1. Highlight rectangle behind the new lines (when any) -----
        double belowY;       // Y to position the expander + buttons row

        if (span.Length > 0)
        {
            // Live span from the tracking-span gives us the user's
            // typed-into region too, so the highlight grows immediately
            // — no save required.
            var firstView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
            var lastPos = span.End > span.Start ? span.End - 1 : span.End;
            var lastView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(lastPos);
            if (firstView is not null && lastView is not null)
            {
                var rect = new Rectangle
                {
                    Fill = HighlightFill,
                    IsHitTestVisible = false,    // text below stays interactive
                    Width = _view.ViewportWidth,
                    Height = lastView.Bottom - firstView.Top,
                };
                Canvas.SetLeft(rect, _view.ViewportLeft);
                Canvas.SetTop(rect, firstView.Top);
                _layer.AddAdornment(
                    AdornmentPositioningBehavior.OwnerControlled,
                    span, AdornmentTagFor(h, "highlight"), rect, null);
                belowY = lastView.Bottom;
            }
            else
            {
                // Lines aren't currently formatted — skip rendering this hunk
                // entirely until the next layout pass when they're in view.
                return;
            }
        }
        else
        {
            // Pure deletion: anchor the panel at the join-point line.
            var view = _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
            if (view is null) return;
            belowY = view.Top;
        }

        var anchorSpan = new SnapshotSpan(span.Start, 0);

        var combined = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        if (h.BaselineLines.Count > 0)
        {
            var expander = BuildGhostExpander(h);
            combined.Children.Add(expander);
        }

        var buttons = BuildButtonRow(h);
        // Anchor the buttons to the top of the row so they stay next to
        // the expander header when the user opens the expander.
        if (buttons is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Top;
            fe.Margin = new Thickness(h.BaselineLines.Count > 0 ? 6 : 0, 0, 0, 0);
        }
        combined.Children.Add(buttons);

        Canvas.SetLeft(combined, _view.ViewportLeft + 4);
        Canvas.SetTop(combined, belowY + 2);
        // Overlay layer (above Text). The row sits in line-space below the
        // hunk's last line; if we put it on the background layer the
        // editor's own text in that row paints over it and the box looks
        // half-transparent, half-missing. Falls back to the background
        // layer if MEF gave us only one (defensive — should never happen).
        (_overlay ?? _layer).AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            anchorSpan, AdornmentTagFor(h, "row"), combined, null);
    }

    // Square 24×24 buttons sized to match the Expander header chrome
    // (FontSize 11 + Padding (6,2,6,2) + ~border ≈ 24px tall) so the
    // accept/reject row lines up perfectly with the "{N} removed lines"
    // header. 6px gap between the two buttons.
    const double SmallButtonWidth = 24;
    const double SmallButtonHeight = 24;
    const double SmallButtonGap = 6;
    const double OpenButtonRowWidth = SmallButtonWidth * 2 + SmallButtonGap;

    FrameworkElement BuildGhostExpander(HunkInfo h)
    {
        var label = h.BaselineLines.Count == 1
            ? "1 removed line"
            : $"{h.BaselineLines.Count} removed lines";

        // Body: a single read-only TextBox so the user can select and copy
        // portions of the original code if they want to restore them by
        // hand. Transparent border / background lets the surrounding wrap
        // panel provide the visual surface, while the TextBox itself stays
        // tab-out-of-the-way (IsTabStop=false).
        var ghostText = new TextBox
        {
            Text = string.Join(Environment.NewLine, h.BaselineLines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(18, 4, 4, 4),
            FontFamily = new FontFamily("Consolas, Lucida Sans Typewriter, Courier New"),
            FontSize = 12,
            Opacity = 0.85,
            IsTabStop = false,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            TextWrapping = TextWrapping.NoWrap,
        };
        ghostText.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        // Wrap the textbox in a Border that paints a solid surface + outline
        // when the expander is open. Without this the Expander's content
        // host is transparent and the text bleeds into the editor below,
        // which is what made it hard to spot.
        var ghostSurface = new Border
        {
            Child = ghostText,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 0, 0),
        };
        ghostSurface.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.DropDownBackgroundBrushKey);
        ghostSurface.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);

        var expander = new Expander
        {
            Header = label,
            IsExpanded = false,        // collapsed by default per spec
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = ghostSurface,
        };
        expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        expander.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.DropDownBackgroundBrushKey);
        expander.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
        return expander;
    }

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
        try { await op(sid, _filePath, h.BaselineStart, h.BaselineCount); }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] AcceptHunk failed: {ex.Message}"); }
    }

    async System.Threading.Tasks.Task OnRejectClicked(HunkInfo h)
    {
        var sid = _service.CurrentSessionId;
        var op = _service.RejectHunkAsync;
        if (sid is null || op is null || _filePath is null) return;
        try { await op(sid, _filePath, h.BaselineStart, h.BaselineCount); }
        catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] RejectHunk failed: {ex.Message}"); }
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

