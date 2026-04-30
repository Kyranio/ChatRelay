using Markdig;
using Markdig.Syntax;
using Microsoft.VisualStudio.PlatformUI;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ChatRelay.Chat.Rendering
{
    /// <summary>
    /// Renders assistant-side markdown into a WPF FlowDocumentScrollViewer.
    /// Parses with Markdig.Wpf, then walks the emitted document to fix its
    /// baked-in colors, apply syntax highlighting to fenced code, and wire up
    /// hyperlink navigation.
    /// </summary>
    internal sealed class MarkdownRenderer
    {
        private readonly FrameworkElement _host;

        public MarkdownRenderer(FrameworkElement host) => _host = host;

        // Markdig.Wpf's curated extension set — enables tables, task lists,
        // strikethrough, autolinks, etc. Shared between rendering and AST queries.
        private static readonly MarkdownPipeline Pipeline =
            Markdig.Wpf.MarkdownExtensions.UseSupportedExtensions(new MarkdownPipelineBuilder()).Build();

        // Semi-transparent gray that reads on both VS dark and light themes.
        // Exposed so the user-bubble renderer in ChatControl can use the
        // same shade for its code-reference borders.
        public static readonly Brush CodeBackground = CreateFrozenBrush(Color.FromArgb(46, 128, 128, 128));

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>
        /// Parses <paramref name="markdown"/> and returns a FlowDocumentScrollViewer
        /// whose internal scrollbars are disabled (the outer chat scroller handles
        /// scrolling across bubbles).
        /// </summary>
        public FlowDocumentScrollViewer Build(string markdown)
        {
            markdown ??= string.Empty;
            var doc = Markdig.Wpf.Markdown.ToFlowDocument(markdown, Pipeline);

            // FlowDocument's default PagePadding adds ~5px around the content,
            // pushing assistant-body text right of the "Claude" label (and right
            // of user-bubble text, which doesn't use a FlowDocument). Zero it so
            // the body aligns with everything else at the bubble's 8px padding.
            doc.PagePadding = new Thickness(0);

            // Markdig.Wpf drops fence language tags and doesn't expose heading
            // level on its emitted Paragraphs — re-parse the markdown AST to
            // recover both, keyed to document order.
            ApplyTheme(doc, ExtractFencedLanguages(markdown), ExtractHeadingLevels(markdown));

            var viewer = new FlowDocumentScrollViewer
            {
                Document = doc,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                IsToolBarVisible = false
            };
            viewer.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ScrollViewer.SetVerticalScrollBarVisibility(viewer, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(viewer, ScrollBarVisibility.Disabled);

            // The inner FlowDocumentScrollViewer swallows mouse wheel events even
            // with its scrollbars disabled — intercept and rebubble to the parent
            // so the outer HistoryScroll handles scrolling across bubbles.
            viewer.PreviewMouseWheel += ForwardWheelToParent;

            // Markdig.Wpf emits <Hyperlink Command="{x:Static md:Commands.Hyperlink}"
            // CommandParameter="url" />. Hook the RoutedCommand to open the URL
            // in the user's default browser.
            viewer.CommandBindings.Add(new CommandBinding(
                Markdig.Wpf.Commands.Hyperlink,
                (s, e) => OpenUrl(e.Parameter as string)));

            return viewer;
        }

        private static void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url!) { UseShellExecute = true });
            }
            catch { /* browser launch failed — swallow silently */ }
        }

        private static void ForwardWheelToParent(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            // Shift+Wheel means the user wants to scroll the code block
            // horizontally — let the inner ScrollViewer receive the event.
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;
            e.Handled = true;
            var rebubble = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            (((FrameworkElement)sender).Parent as UIElement)?.RaiseEvent(rebubble);
        }

        // Markdig.Wpf bakes these values (from Themes/Generic.xaml) during its
        // internal XAML parse, so UserControl.Resources overrides can't reach them.
        // Walk the document after parsing and fix them up:
        //
        //   CodeBlockStyleKey   Background  = #FFD3D3D3  → paragraph replaced entirely (ReplaceCodeParagraph)
        //   CodeStyleKey        Background  = #FFD3D3D3  → run re-painted (RethemeInline)
        //   Heading 1–4         Foreground  = #FF000000  → retargeted to theme text brush
        //   ThematicBreak       Stroke      = #FF000000  → retargeted
        //   Table / TableCell   BorderBrush = #FF000000  → retargeted
        private void ApplyTheme(FlowDocument doc, Queue<string> codeLanguages, Queue<int> headingLevels)
        {
            // Snapshot the top-level block list before iterating — we may mutate
            // doc.Blocks by replacing code paragraphs.
            var snapshot = doc.Blocks.ToList();
            foreach (var block in snapshot) RethemeBlock(doc.Blocks, block, codeLanguages, headingLevels);
        }

        private void RethemeBlock(BlockCollection parent, System.Windows.Documents.Block block,
                                  Queue<string> codeLanguages, Queue<int> headingLevels)
        {
            // EVERY collection on a TextElement (Inlines, Blocks, ListItems,
            // RowGroups, Rows, Cells, …) is a versioned WPF collection. Setting
            // a property via SetResourceReference / ApplyHeadingStyle during a
            // theme walk can cause WPF to internally rebalance / merge runs,
            // which bumps the enclosing collection's version and trips the
            // enumerator on the next MoveNext. Snapshot before iterating.
            switch (block)
            {
                case Paragraph p when IsCodeFont(p.FontFamily):
                    ReplaceCodeParagraph(parent, p, codeLanguages);
                    break;
                case Paragraph p when p.Style != null && headingLevels.Count > 0:
                    // Markdig.Wpf applies a Heading*StyleKey via SetResourceReference
                    // for each heading block; regular paragraphs have no style. Match
                    // with the AST-extracted level and apply consistent bold styling.
                    ApplyHeadingStyle(p, headingLevels.Dequeue());
                    foreach (var inline in p.Inlines.ToList()) RethemeInline(inline);
                    break;
                case Paragraph p:
                    if (IsStyleAppliedBlack(p, Paragraph.ForegroundProperty))
                        p.SetResourceReference(Paragraph.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    foreach (var inline in p.Inlines.ToList()) RethemeInline(inline);
                    break;
                case Section s:
                    foreach (var child in s.Blocks.ToList())
                        RethemeBlock(s.Blocks, child, codeLanguages, headingLevels);
                    break;
                case List l:
                    foreach (var item in l.ListItems.ToList())
                        foreach (var child in item.Blocks.ToList())
                            RethemeBlock(item.Blocks, child, codeLanguages, headingLevels);
                    break;
                case Table t:
                    if (IsStyleAppliedBlack(t, System.Windows.Documents.Block.BorderBrushProperty))
                        t.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    foreach (var group in t.RowGroups.ToList())
                        foreach (var row in group.Rows.ToList())
                            foreach (var cell in row.Cells.ToList())
                            {
                                if (IsStyleAppliedBlack(cell, TableCell.BorderBrushProperty))
                                    cell.SetResourceReference(TableCell.BorderBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
                                foreach (var cb in cell.Blocks.ToList())
                                    RethemeBlock(cell.Blocks, cb, codeLanguages, headingLevels);
                            }
                    break;
                case BlockUIContainer u when u.Child is System.Windows.Shapes.Line line:
                    // Thematic break (<hr/>-equivalent).
                    if (IsStyleAppliedBlack(line, System.Windows.Shapes.Shape.StrokeProperty))
                        line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    break;
            }
        }

        // Override Markdig's per-level heading styling with a consistent bold
        // scale. Local values win over its Style setters.
        private static void ApplyHeadingStyle(Paragraph p, int level)
        {
            p.FontWeight = FontWeights.Bold;
            p.TextDecorations = null; // kill Markdig's H4 underline
            p.FontSize = level switch
            {
                1 => 22,
                2 => 18,
                3 => 16,
                4 => 14,
                5 => 13,
                _ => 12, // 6 or higher
            };
            p.Margin = new Thickness(0, 8, 0, 4);
            p.SetResourceReference(Paragraph.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }

        // Replace a Markdig code Paragraph with Border { TextBlock { highlighted inlines } }
        // inside a BlockUIContainer. This fixes two problems at once:
        //   1) Paragraph.Background paints per-line box (leaving gaps / bleed);
        //      a Border paints a solid rectangle.
        //   2) Padding/CornerRadius are honored properly on a Border.
        // The language tag comes from the AST queue built in ExtractFencedLanguages.
        private void ReplaceCodeParagraph(BlockCollection parent, Paragraph code, Queue<string> codeLanguages)
        {
            var text = ExtractParagraphText(code);
            var language = codeLanguages.Count > 0 ? codeLanguages.Dequeue() : string.Empty;

            var textBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Lucida Sans Typewriter, Courier New"),
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, 4) // gap above the scrollbar
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            if (CodeHighlighter.TryHighlight(text, language, out var inlines) && inlines != null)
            {
                foreach (var il in inlines) textBlock.Inlines.Add(il);
            }
            else
            {
                textBlock.Text = text;
            }

            // ScrollViewer gives wide code a horizontal scrollbar instead of
            // forcing the bubble wider. Vertical is disabled since the outer
            // HistoryScroll handles that; horizontal appears only when needed.
            var scroll = new ScrollViewer
            {
                Content = textBlock,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            // Shift+Wheel = horizontal scroll (WPF doesn't do this by default).
            scroll.PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift) return;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            var copyButton = BuildCopyButton(text);

            // Grid lets the copy button overlay the scroll viewer at top-right.
            var grid = new Grid();
            grid.Children.Add(scroll);
            grid.Children.Add(copyButton);

            var border = new Border
            {
                Background = CodeBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4),
                Child = grid
            };

            // Reveal the button while the mouse is over the whole code block.
            border.MouseEnter += (s, e) => copyButton.Opacity = 1;
            border.MouseLeave += (s, e) => copyButton.Opacity = 0;

            var container = new BlockUIContainer(border);
            parent.InsertAfter(code, container);
            parent.Remove(code);
        }

        private Button BuildCopyButton(string code)
        {
            const string copyGlyph = "\u2398"; // U+2398 NEXT PAGE (copy-icon shape)

            var button = new Button
            {
                Content = copyGlyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Opacity = 0,
                ToolTip = "Copy",
                Style = _host.TryFindResource("CopyButtonStyle") as Style
            };
            button.Click += (s, e) =>
            {
                try { Clipboard.SetText(code); }
                catch { /* clipboard can be briefly locked by other apps — swallow */ }

                // Visual ack: swap glyph for a checkmark for ~1.2s then revert.
                button.Content = "\u2713"; // ✓
                button.ToolTip = "Copied";
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = System.TimeSpan.FromMilliseconds(1200)
                };
                timer.Tick += (ts, te) =>
                {
                    button.Content = copyGlyph;
                    button.ToolTip = "Copy";
                    timer.Stop();
                };
                timer.Start();
            };
            return button;
        }

        // Fenced code is a single Paragraph with xml:space="preserve". Handle
        // both the Run-list and fallback-TextRange shapes defensively.
        private static string ExtractParagraphText(Paragraph p)
        {
            var sb = new StringBuilder();
            foreach (var inline in p.Inlines)
            {
                switch (inline)
                {
                    case Run r: sb.Append(r.Text); break;
                    case LineBreak _: sb.AppendLine(); break;
                }
            }
            if (sb.Length == 0)
            {
                // Fallback: use TextRange over the paragraph's own content.
                var range = new TextRange(p.ContentStart, p.ContentEnd);
                sb.Append(range.Text);
            }
            return sb.ToString();
        }

        private static Queue<string> ExtractFencedLanguages(string markdown)
        {
            var q = new Queue<string>();
            foreach (var fenced in Markdown.Parse(markdown, Pipeline).Descendants<FencedCodeBlock>())
                q.Enqueue(fenced.Info ?? string.Empty);
            return q;
        }

        private static Queue<int> ExtractHeadingLevels(string markdown)
        {
            var q = new Queue<int>();
            foreach (var h in Markdown.Parse(markdown, Pipeline).Descendants<HeadingBlock>())
                q.Enqueue(h.Level);
            return q;
        }

        private static void RethemeInline(Inline inline)
        {
            if (inline is Run r && IsCodeFont(r.FontFamily))
            {
                r.Background = CodeBackground;
            }
            else if (inline is Span s)
            {
                // Snapshot — see RethemeBlock for why iterating an
                // unsnapshotted WPF text-element collection during themeing
                // is unsafe.
                foreach (var child in s.Inlines.ToList()) RethemeInline(child);
            }
        }

        private static bool IsCodeFont(FontFamily family)
            => family?.Source?.StartsWith("Consolas", System.StringComparison.Ordinal) == true;

        // True when a property's effective value is Black AND that value comes
        // from a Style (not a local SetValue). This distinguishes Markdig's
        // baked heading / table / break colors from properly inherited theme
        // brushes, which we don't want to touch.
        private static bool IsStyleAppliedBlack(DependencyObject obj, DependencyProperty prop)
        {
            var source = DependencyPropertyHelper.GetValueSource(obj, prop);
            if (source.BaseValueSource != BaseValueSource.Style
                && source.BaseValueSource != BaseValueSource.StyleTrigger) return false;
            return obj.GetValue(prop) is SolidColorBrush b && b.Color == Colors.Black;
        }
    }
}
