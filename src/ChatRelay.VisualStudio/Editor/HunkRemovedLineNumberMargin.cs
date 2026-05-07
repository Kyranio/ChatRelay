using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatRelay.Host;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>Provides a sibling margin to LineNumber that shows the original baseline line numbers for the removed-line strips.</summary>
[Export(typeof(IWpfTextViewMarginProvider))]
[Name(HunkRemovedLineNumberMargin.MarginName)]
[Order(After = PredefinedMarginNames.LineNumber)]
[MarginContainer(PredefinedMarginNames.LeftSelection)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
public sealed class HunkRemovedLineNumberMarginProvider : IWpfTextViewMarginProvider
{
    [Import]
    public IClassificationFormatMapService FormatMapService { get; set; } = null!;

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin? parent) =>
        new HunkRemovedLineNumberMargin(host.TextView, FormatMapService.GetClassificationFormatMap(host.TextView));
}

/// <summary>Paints baseline line numbers in the gap that <see cref="HunkRemovedLinesTagger"/> reserves above each open-hunk anchor line.</summary>
internal sealed class HunkRemovedLineNumberMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "ChatRelayHunkRemovedLineNumbers";

    static readonly Brush GutterText = Frozen(new SolidColorBrush(Color.FromRgb(0x96, 0x55, 0x55)));

    readonly IWpfTextView _view;
    readonly IClassificationFormatMap _formatMap;
    readonly EditorChangesService _service;
    string? _filePath;
    bool _disposed;

    public HunkRemovedLineNumberMargin(IWpfTextView view, IClassificationFormatMap formatMap)
    {
        _view = view;
        _formatMap = formatMap;
        _service = EditorChangesService.Current;
        _filePath = ResolveFilePath(view);
        ClipToBounds = true;
        Width = 0;

        _view.LayoutChanged += OnLayoutChanged;
        _service.HunksChanged += OnHunksChanged;
        _formatMap.ClassificationFormatMappingChanged += OnFormatMappingChanged;
        _view.Closed += OnViewClosed;
    }

    public double MarginSize => Width;
    public bool Enabled => true;
    public FrameworkElement VisualElement => this;

    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.LayoutChanged -= OnLayoutChanged;
        _service.HunksChanged -= OnHunksChanged;
        _formatMap.ClassificationFormatMappingChanged -= OnFormatMappingChanged;
        _view.Closed -= OnViewClosed;
    }

    void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) => Render();

    void OnHunksChanged(string changedPath)
    {
        if (_filePath is null) _filePath = ResolveFilePath(_view);
        if (_filePath is null) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        Render();
    }

    void OnFormatMappingChanged(object? sender, EventArgs e) => Render();
    void OnViewClosed(object? sender, EventArgs e) => Dispose();

    void Render()
    {
        Children.Clear();
        if (_disposed) return;

        if (_filePath is null) _filePath = ResolveFilePath(_view);
        if (_filePath is null) { Width = 0; return; }

        var hunks = _service.GetHunks(_filePath);
        if (hunks.Count == 0) { Width = 0; return; }

        var snapshot = _view.TextSnapshot;
        var defaults = _formatMap.DefaultTextProperties;
        var typeface = defaults.Typeface;
        double fontSize = defaults.FontRenderingEmSize;
        double lineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
        double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? (fontSize * 0.6);

        // Width = enough columns to fit the largest visible removed-line number, plus a small right gap.
        int maxDigits = 1;
        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            if (string.Equals(h.State, "accepted", StringComparison.Ordinal)) continue;
            int max = h.BaselineStart + h.BaselineLines.Count;
            int digits = max < 10 ? 1 : max < 100 ? 2 : max < 1000 ? 3 : max < 10000 ? 4 : 5;
            if (digits > maxDigits) maxDigits = digits;
        }
        Width = maxDigits * columnWidth + 6;

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            if (string.Equals(h.State, "accepted", StringComparison.Ordinal)) continue;

            int anchorLine = h.CurrentStart;
            if (anchorLine < 0 || anchorLine >= snapshot.LineCount) continue;

            var anchorPos = snapshot.GetLineFromLineNumber(anchorLine).Start;
            var view = _view.TextViewLines.GetTextViewLineContainingBufferPosition(anchorPos);
            if (view is null) continue;

            // Reserved gap = [Top, TextTop]. If the tagger hasn't reserved anything, skip.
            double reservedTop = view.Top;
            double reservedHeight = view.TextTop - view.Top;
            if (reservedHeight < lineHeight - 2) continue;

            // Each baseline line gets one editor-line-height row inside the reserved gap.
            for (int i = 0; i < h.BaselineLines.Count; i++)
            {
                var tb = new TextBlock
                {
                    Text = (h.BaselineStart + i + 1).ToString(),
                    Foreground = GutterText,
                    FontFamily = typeface.FontFamily,
                    FontStyle = typeface.Style,
                    FontWeight = typeface.Weight,
                    FontStretch = typeface.Stretch,
                    FontSize = fontSize,
                    TextAlignment = TextAlignment.Right,
                    Width = Width - 4,
                };
                SetLeft(tb, 0);
                // Match the strip's own VerticalPadding so the numbers line up with the strip's text rows.
                SetTop(tb, reservedTop + i * lineHeight + 2);
                Children.Add(tb);
            }
        }
    }

    static string? ResolveFilePath(IWpfTextView view)
    {
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
