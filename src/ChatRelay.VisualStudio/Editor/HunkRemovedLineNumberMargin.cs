using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChatRelay.Host;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>Sibling margin to LineNumber that paints baseline line numbers in the gap reserved by HunkRemovedLinesTagger.</summary>
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
        _filePath ??= ResolveFilePath(_view);
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

        _filePath ??= ResolveFilePath(_view);
        if (_filePath is null) { Width = 0; return; }

        var hunks = _service.GetHunks(_filePath);
        if (hunks.Count == 0) { Width = 0; return; }

        var snapshot = _view.TextSnapshot;
        var defaults = _formatMap.DefaultTextProperties;
        var typeface = defaults.Typeface;
        double fontSize = defaults.FontRenderingEmSize;
        double lineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
        double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? (fontSize * 0.6);

        int maxDigits = 1;
        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;
            int max = h.BaselineStart + h.BaselineLines.Count;
            int digits = max < 10 ? 1 : max < 100 ? 2 : max < 1000 ? 3 : max < 10000 ? 4 : 5;
            if (digits > maxDigits) maxDigits = digits;
        }
        Width = maxDigits * columnWidth + 6;

        foreach (var h in hunks)
        {
            if (h.BaselineLines.Count == 0) continue;

            int anchorLine = h.CurrentStart;
            if (anchorLine < 0 || anchorLine >= snapshot.LineCount) continue;

            // Mirror HunkRemovedLinesTagger: anchor on N-1 with bottom-space, fall back to above-line-0 at file top.
            SnapshotPoint anchorPos;
            bool isAboveLine;
            if (anchorLine > 0)
            {
                anchorPos = snapshot.GetLineFromLineNumber(anchorLine - 1).Start;
                isAboveLine = false;
            }
            else
            {
                anchorPos = snapshot.GetLineFromLineNumber(anchorLine).Start;
                isAboveLine = true;
            }

            var view = _view.TextViewLines.GetTextViewLineContainingBufferPosition(anchorPos);
            if (view is null) continue;

            // ITextViewLine coordinates are in buffer space. Adornment layers absorb the scroll transform implicitly;
            // this margin's Canvas sits outside that transform, so subtract ViewportTop ourselves or numbers stick on scroll.
            double reservedTop = isAboveLine ? view.Top - _view.ViewportTop : view.TextBottom - _view.ViewportTop;
            double reservedHeight = isAboveLine ? view.TextTop - view.Top : view.Bottom - view.TextBottom;
            if (reservedHeight < lineHeight - 2) continue;

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
                // +2 matches the strip's own VerticalPadding so numbers align with strip rows.
                SetTop(tb, reservedTop + i * lineHeight + 2);
                Children.Add(tb);
            }
        }
    }

    static string? ResolveFilePath(IWpfTextView view) =>
        view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc) ? doc.FilePath : null;

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
