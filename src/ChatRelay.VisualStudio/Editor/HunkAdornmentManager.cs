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
    readonly IAdornmentLayer? _layer;

    // Last hunk list we received from the service, kept so we can re-render
    // on LayoutChanged without re-querying. Empty list while we have nothing
    // to show.
    IReadOnlyList<HunkInfo> _hunks = Array.Empty<HunkInfo>();

    public HunkAdornmentManager(IWpfTextView view)
    {
        _view = view;
        _filePath = ResolveFilePath(view);
        _service = EditorChangesService.Current;
        try
        {
            _layer = view.GetAdornmentLayer(HunkAdornmentLayerDefinition.LayerName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] Failed to acquire adornment layer: {ex.Message}");
            _layer = null;
        }

        if (_filePath is null) return;

        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;
        _view.LayoutChanged += OnLayoutChanged;

        // Pick up any hunks already known (the snapshot may have arrived
        // before the editor view opened).
        _hunks = _service.GetHunks(_filePath);
        if (_hunks.Count > 0) RenderHunks();
    }

    void OnHunksChanged(string changedPath)
    {
        if (_filePath is null) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        _hunks = _service.GetHunks(_filePath);
        Debug.WriteLine($"[ChatRelay.editor] Hunks for {_filePath}: {_hunks.Count} hunk(s)");
        RenderHunks();
    }

    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        // Re-render on every layout change so adornments follow scroll,
        // resize, and text edits. Phase 4.4 will refine this to re-anchor
        // hunks against the live buffer (handling user typing inside the
        // hunk's region); for 4.3 we simply redraw at the snapshot's
        // current line numbers.
        if (_hunks.Count == 0) return;
        RenderHunks();
    }

    void RenderHunks()
    {
        if (_layer is null) return;
        _layer.RemoveAllAdornments();

        var snapshot = _view.TextSnapshot;
        foreach (var h in _hunks)
        {
            try
            {
                if (string.Equals(h.State, "accepted", StringComparison.Ordinal))
                    RenderAcceptedHunk(h, snapshot);
                else
                    RenderOpenHunk(h, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRelay.editor] Render hunk failed: {ex.Message}");
            }
        }
    }

    void RenderAcceptedHunk(HunkInfo h, ITextSnapshot snapshot)
    {
        if (_layer is null) return;
        // Accepted hunk marker:
        //   - thin turquoise vertical bar in the left margin of the lines
        //     the model produced
        //   - tooltip "Edited by {Model}" — model resolved from the hunk's
        //     Model field (carried on the wire by Phase 4.3a)
        //   - reject (↶) button below the region so the user can still
        //     change their mind from inside the editor
        if (h.CurrentCount <= 0
            || h.CurrentStart < 0
            || h.CurrentStart + h.CurrentCount > snapshot.LineCount)
            return;

        var firstLine = snapshot.GetLineFromLineNumber(h.CurrentStart);
        var lastLine = snapshot.GetLineFromLineNumber(h.CurrentStart + h.CurrentCount - 1);
        var span = new SnapshotSpan(firstLine.Start, lastLine.End);

        var firstView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(firstLine.Start);
        var lastView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(lastLine.Start);
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

        // Reject button below the region — same 64×32 secondary style as
        // open hunks, so the user can still revert. Accept button is
        // intentionally absent (already accepted).
        var reject = BuildIconButton("↶", primary: false);
        reject.ToolTip = $"Revert this change ({modelLabel})";
        reject.Click += async (_, _) => await OnRejectClicked(h);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(reject);
        Canvas.SetLeft(row, _view.ViewportLeft + 8);
        Canvas.SetTop(row, lastView.Bottom + 2);
        _layer.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            span, AdornmentTagFor(h, "accepted-reject"), row, null);
    }

    void RenderOpenHunk(HunkInfo h, ITextSnapshot snapshot)
    {
        if (_layer is null) return;

        // ---- 1. Highlight rectangle behind the new lines (when any) -----
        double belowY;       // Y to position the panel/buttons block

        if (h.CurrentCount > 0
            && h.CurrentStart >= 0
            && h.CurrentStart + h.CurrentCount <= snapshot.LineCount)
        {
            var firstLine = snapshot.GetLineFromLineNumber(h.CurrentStart);
            var lastLine = snapshot.GetLineFromLineNumber(h.CurrentStart + h.CurrentCount - 1);
            var span = new SnapshotSpan(firstLine.Start, lastLine.End);

            // Geometry only exists for lines currently in the formatted
            // view region — off-screen hunks return null. That's fine,
            // they re-render when the user scrolls them in.
            var firstView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(firstLine.Start);
            var lastView = _view.TextViewLines.GetTextViewLineContainingBufferPosition(lastLine.Start);
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
            // Pure deletion: anchor the panel where the deletion happened.
            // CurrentStart is the line that follows what was deleted.
            int anchorLine = Math.Max(0, Math.Min(h.CurrentStart, snapshot.LineCount - 1));
            var line = snapshot.GetLineFromLineNumber(anchorLine);
            var view = _view.TextViewLines.GetTextViewLineContainingBufferPosition(line.Start);
            if (view is null) return;
            belowY = view.Top;
        }

        // ---- 2. Panel below: optional ghost expander + button row -------

        var panel = BuildBelowPanel(h);
        // Anchor at the left edge of the viewport so the buttons sit at
        // the start of the editor pane regardless of scroll. A small
        // top margin gives breathing room from the highlight.
        Canvas.SetLeft(panel, _view.ViewportLeft + 4);
        Canvas.SetTop(panel, belowY + 2);

        var anchorSpan = new SnapshotSpan(
            snapshot.GetLineFromLineNumber(Math.Max(0, h.CurrentStart)).Start, 0);
        _layer.AddAdornment(
            AdornmentPositioningBehavior.OwnerControlled,
            anchorSpan, AdornmentTagFor(h, "panel"), panel, null);
    }

    FrameworkElement BuildBelowPanel(HunkInfo h)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };

        // Ghost expander (only when there are old lines to show).
        if (h.BaselineLines.Count > 0)
        {
            stack.Children.Add(BuildGhostExpander(h));
        }

        stack.Children.Add(BuildButtonRow(h));
        return stack;
    }

    FrameworkElement BuildGhostExpander(HunkInfo h)
    {
        var label = h.BaselineLines.Count == 1
            ? "1 removed line"
            : $"{h.BaselineLines.Count} removed lines";

        // Body: each old line as a row, greyed out (no strikethrough).
        var ghost = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(18, 4, 4, 4) };
        foreach (var line in h.BaselineLines)
        {
            var tb = new TextBlock
            {
                Text = string.IsNullOrEmpty(line) ? " " : line,    // keep height for blank lines
                FontFamily = new FontFamily("Consolas, Lucida Sans Typewriter, Courier New"),
                FontSize = 12,
                Opacity = 0.55,
                TextWrapping = TextWrapping.NoWrap,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ghost.Children.Add(tb);
        }

        var expander = new Expander
        {
            Header = label,
            IsExpanded = false,        // collapsed by default per spec
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = ghost,
        };
        expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        expander.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        expander.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
        return expander;
    }

    UIElement BuildButtonRow(HunkInfo h)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var accept = BuildIconButton("✓", primary: true);
        accept.ToolTip = "Accept this change";
        accept.Click += async (_, _) => await OnAcceptClicked(h);
        row.Children.Add(accept);

        var reject = BuildIconButton("↶", primary: false);
        reject.ToolTip = "Revert this change";
        reject.Margin = new Thickness(6, 0, 0, 0);
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

    // 64×32 icon-only buttons — accept gets the brand turquoise, reject
    // gets a theme-bound neutral surface. Custom ControlTemplate so WPF's
    // default hover/pressed gradients don't override our colors.
    static Button BuildIconButton(string glyph, bool primary)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = 64,
            Height = 32,
            FontSize = 16,
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
    }

    static string? ResolveFilePath(IWpfTextView view)
    {
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }
}

