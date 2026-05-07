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
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace ChatRelay.Editor;

/// <summary>
/// Per-editor-view renderer for the turquoise highlight (open hunks),
/// the accepted-hunk marker bar, and the accept/reject button row.
/// Listens to <see cref="EditorChangesService.HunksChanged"/> for its
/// file plus <see cref="IWpfTextView.LayoutChanged"/> /
/// <see cref="ITextBuffer.Changed"/>, repaints accordingly, and routes
/// button clicks back to the host via the service callbacks.
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
    readonly EditorChangesService _service;
    // Background layer (below Text): highlight + accepted marker.
    // Overlay layer (above Text): buttons — kept above so editor text in
    // the row doesn't paint over them.
    readonly IAdornmentLayer? _layer;
    readonly IAdornmentLayer? _overlay;
    // Button rows live in a popup space-reservation agent, not the
    // adornment layer, so they survive scrolling past the hunk's edges
    // (manual Canvas placement clipped at the viewport bottom for hunks
    // taller than the visible area).
    readonly ISpaceReservationManager? _buttonManager;
    readonly List<ISpaceReservationAgent> _buttonAgents = new();

    // Tracked hunks carry an ITrackingSpan so the highlight/buttons
    // follow buffer edits live without waiting for a host snapshot.
    // LocallyInvalidated suppresses the marker between the user typing
    // into an accepted region and the host catching up on next
    // snapshot — the InvalidateAcceptedHunk RPC drives that catch-up.
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

        try
        {
            _buttonManager = view.GetSpaceReservationManager(HunkButtonsSpaceReservationManagerDefinition.Name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] Failed to acquire button popup manager: {ex.Message}");
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
        _filePath = ResolveFilePath(_view);
        if (_filePath is null) return false;
        RebuildTrackedFromService();
        if (_tracked.Count > 0) RenderHunks();
        return true;
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

    // The watcher / host snapshot path can't see in-buffer edits until
    // the user saves. Two visual responses:
    //   • Accepted hunk intersected by typing → suppress its marker
    //     locally; the InvalidateAcceptedHunk RPC tells the host so the
    //     next snapshot reports it as gone.
    //   • Open hunk whose model new-lines were ALL undone / backspaced
    //     out of the buffer → suppress locally; on save the host's diff
    //     naturally drops the hunk.
    // Open hunks that simply grew or shrank by user typing need no
    // special handling — tracking spans follow automatically.
    void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        if (!EnsureFilePath()) return;
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
                // IntersectsWith is a strict superset of OverlapsWith —
                // it also matches zero-width touch at edges, which is
                // what we want for "did this change reach the hunk".
                if (change.NewSpan.IntersectsWith(current.Span))
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
        // Fire-and-forget on a worker thread so JsonRpc exceptions don't
        // surface on the UI thread.
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
        ClearButtonAgents();

        var snapshot = _view.TextSnapshot;
        foreach (var t in _tracked)
        {
            // Skip accepted hunks the user has typed into.
            if (t.LocallyInvalidated) continue;

            try
            {
                var live = t.Span.GetSpan(snapshot);
                if (string.Equals(t.Info.State, "accepted", StringComparison.Ordinal))
                    RenderAcceptedHunk(t.Info, live, snapshot);
                else
                    RenderOpenHunk(t, live, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatRelay.editor] Render hunk failed: {ex.Message}");
            }
        }
    }

    void ClearButtonAgents()
    {
        if (_buttonManager is null || _buttonAgents.Count == 0) return;
        foreach (var agent in _buttonAgents)
        {
            try { _buttonManager.RemoveAgent(agent); }
            catch (Exception ex) { Debug.WriteLine($"[ChatRelay.editor] RemoveAgent failed: {ex.Message}"); }
        }
        _buttonAgents.Clear();
    }

    // Accepted hunks render only as a thin turquoise marker bar in the
    // left margin (no reject button — accepts are set in stone; the user
    // reverts via the editor's own undo stack).
    void RenderAcceptedHunk(HunkInfo h, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;
        if (span.Length <= 0) return;

        // Intersect-the-span (vs Get…ContainingBufferPosition) keeps the
        // marker visible when the hunk is partially scrolled off-screen.
        var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
        if (formatted is null || formatted.Count == 0) return;
        var firstView = formatted[0];
        var lastView = formatted[formatted.Count - 1];

        var modelLabel = string.IsNullOrEmpty(h.Model)
            ? "Edited by the model"
            : $"Edited by {h.Model}";

        // TextTop / TextBottom (the glyph band) rather than Top / Bottom
        // — the latter includes any reserved top-space the
        // InterLineAdornmentTagger has set above the line, and we don't
        // want the marker bar to extend into that gap.
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

    void RenderOpenHunk(TrackedHunk t, SnapshotSpan span, ITextSnapshot snapshot)
    {
        if (_layer is null) return;
        var h = t.Info;

        if (span.Length > 0)
        {
            // Intersect-the-span keeps the highlight visible when the
            // hunk is partially scrolled off-screen.
            var formatted = _view.TextViewLines.GetTextViewLinesIntersectingSpan(span);
            if (formatted is null || formatted.Count == 0)
            {
                // Hunk fully off-screen — still create the popup so the
                // buttons stay reachable when scrolled close enough.
                AddButtonPopup(h, t.Span);
                return;
            }
            var firstView = formatted[0];
            var lastView = formatted[formatted.Count - 1];

            // TextTop / TextBottom — Top includes any reserved top-space
            // VS allocated above the line for HunkRemovedLinesTagger's
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
                span, AdornmentTagFor(h, "highlight"), rect, null);
        }

        AddButtonPopup(h, t.Span);
    }

    void AddButtonPopup(HunkInfo h, ITrackingSpan trackingSpan)
    {
        if (_buttonManager is null) return;
        var buttons = BuildButtonRow(h);
        try
        {
            // PreferLeftOrTopPosition + RightOrBottomJustify pins the popup
            // to the BOTTOM-RIGHT corner of the hunk's tracked span — the
            // same place the inline button row used to live, but rendered
            // as a Win32 popup so it stays reachable when the span runs
            // taller than the viewport.
            var agent = _buttonManager.CreatePopupAgent(
                trackingSpan,
                PopupStyles.RightOrBottomJustify,
                buttons);
            _buttonManager.AddAgent(agent);
            _buttonAgents.Add(agent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChatRelay.editor] CreatePopupAgent failed: {ex.Message}");
        }
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
        ClearButtonAgents();
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

