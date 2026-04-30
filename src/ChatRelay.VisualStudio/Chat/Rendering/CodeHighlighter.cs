using ColorCodeStandard;
using ColorCodeStandard.Common;
using ColorCodeStandard.Parsing;
using System.Collections.Generic;
using System.IO;
using System.Windows.Documents;
using System.Windows.Media;

namespace ChatRelay.Chat.Rendering
{
    /// <summary>
    /// Syntax-highlights a code string for a given language tag and returns a
    /// list of WPF <see cref="Inline"/> runs suitable for adding to a TextBlock.
    /// Returns false when the language isn't supported; callers should fall back
    /// to a plain text run.
    /// </summary>
    internal static class CodeHighlighter
    {
        public static bool TryHighlight(string code, string languageTag, out List<Inline>? inlines)
        {
            inlines = null;
            var lang = ResolveLanguage(languageTag);
            if (lang == null) return false;

            var formatter = new WpfInlineFormatter();
            // StyleSheet is required by the API but our formatter doesn't consult
            // it — we use our own color map. Any non-null value will do.
            var colorizer = new CodeColorizer();
            colorizer.Colorize(code, lang, formatter, StyleSheets.Default, TextWriter.Null);

            inlines = formatter.Inlines;
            return inlines.Count > 0;
        }

        // Maps markdown fence tags (```csharp, ```cs, ```js, …) to ColorCode's
        // language registry. Unsupported tags return null so the caller can
        // fall back to plain text.
        private static ILanguage? ResolveLanguage(string? tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var t = tag!.Trim().ToLowerInvariant();
            switch (t)
            {
                case "cs":
                case "csharp":
                case "c#":            return Languages.CSharp;
                case "js":
                case "javascript":    return Languages.JavaScript;
                case "json":          return Languages.JavaScript;   // JSON ≈ JS literals — good enough
                case "ts":
                case "typescript":    return Languages.JavaScript;   // close-enough fallback
                case "java":          return Languages.Java;
                case "cpp":
                case "c++":
                case "cxx":
                case "c":             return Languages.Cpp;
                case "html":
                case "htm":           return Languages.Html;
                case "xml":
                case "xaml":
                case "svg":           return Languages.Xml;
                case "css":           return Languages.Css;
                case "sql":           return Languages.Sql;
                case "php":           return Languages.Php;
                case "ps1":
                case "powershell":    return Languages.PowerShell;
                case "vb":
                case "vbnet":
                case "vb.net":        return Languages.VbDotNet;
                default:              return Languages.FindById(t);
            }
        }
    }

    /// <summary>
    /// ColorCode <see cref="IFormatter"/> that emits WPF <see cref="Run"/>s into
    /// an in-memory list instead of writing HTML. The API requires a TextWriter
    /// (unused here) and an IStyleSheet (ignored — we use our own palette).
    /// </summary>
    internal sealed class WpfInlineFormatter : IFormatter
    {
        public List<Inline> Inlines { get; } = new List<Inline>();

        public void Write(string parsedSourceCode, IList<Scope> scopes, IStyleSheet styleSheet, TextWriter buffer)
        {
            // ColorCode may call this once with the full source and nested scopes,
            // or multiple times per chunk. Either way we walk the buffer and emit
            // one Run per deepest-scope region (or per gap between scopes).
            int pos = 0;
            int len = parsedSourceCode.Length;
            while (pos < len)
            {
                var scope = FindDeepestScope(scopes, pos);
                if (scope != null)
                {
                    int end = System.Math.Min(scope.Index + scope.Length, len);
                    Inlines.Add(CreateRun(parsedSourceCode.Substring(pos, end - pos), scope.Name));
                    pos = end;
                }
                else
                {
                    int nextStart = NextScopeStart(scopes, pos, len);
                    Inlines.Add(CreateRun(parsedSourceCode.Substring(pos, nextStart - pos), null));
                    pos = nextStart;
                }
            }
        }

        public void WriteFooter(IStyleSheet styleSheet, ILanguage language, TextWriter buffer) { }
        public void WriteHeader(IStyleSheet styleSheet, ILanguage language, TextWriter buffer) { }

        // Walk the scope tree for the innermost scope covering pos.
        private static Scope? FindDeepestScope(IList<Scope> scopes, int pos)
        {
            Scope? best = null;
            if (scopes == null) return null;
            foreach (var s in scopes)
            {
                if (pos >= s.Index && pos < s.Index + s.Length)
                {
                    best = s;
                    var child = FindDeepestScope(s.Children, pos);
                    if (child != null) best = child;
                }
            }
            return best;
        }

        private static int NextScopeStart(IList<Scope> scopes, int pos, int max)
        {
            int best = max;
            if (scopes == null) return best;
            foreach (var s in scopes)
                if (s.Index > pos && s.Index < best) best = s.Index;
            return best;
        }

        private static Run CreateRun(string text, string? scopeName)
        {
            var run = new Run(text);
            if (scopeName != null && CodeTheme.TryGetValue(scopeName, out var b))
                run.Foreground = b;
            return run;
        }

        // Medium-value colors chosen to be readable on both VS dark and light
        // themes. Close to the VS Code Dark+ palette — most work on light too
        // because they're mid-brightness rather than very pale.
        private static readonly Dictionary<string, Brush> CodeTheme = BuildTheme();

        private static Dictionary<string, Brush> BuildTheme()
        {
            Brush F(string hex)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var br = new SolidColorBrush(c);
                br.Freeze();
                return br;
            }
            return new Dictionary<string, Brush>
            {
                [ScopeName.Keyword]               = F("#569CD6"),
                [ScopeName.PreprocessorKeyword]   = F("#C586C0"),
                [ScopeName.Comment]               = F("#6A9955"),
                [ScopeName.HtmlComment]           = F("#6A9955"),
                [ScopeName.XmlComment]            = F("#6A9955"),
                [ScopeName.XmlDocComment]         = F("#6A9955"),
                [ScopeName.XmlDocTag]             = F("#6A9955"),
                [ScopeName.String]                = F("#CE9178"),
                [ScopeName.StringCSharpVerbatim]  = F("#CE9178"),
                [ScopeName.ClassName]             = F("#4EC9B0"),
                [ScopeName.PowerShellType]        = F("#4EC9B0"),
                [ScopeName.HtmlElementName]       = F("#569CD6"),
                [ScopeName.HtmlAttributeName]     = F("#9CDCFE"),
                [ScopeName.HtmlAttributeValue]    = F("#CE9178"),
                [ScopeName.HtmlTagDelimiter]      = F("#808080"),
                [ScopeName.XmlName]               = F("#569CD6"),
                [ScopeName.XmlAttribute]          = F("#9CDCFE"),
                [ScopeName.XmlAttributeValue]    = F("#CE9178"),
                [ScopeName.XmlAttributeQuotes]    = F("#CE9178"),
                [ScopeName.XmlDelimiter]          = F("#808080"),
                [ScopeName.XmlCDataSection]       = F("#808080"),
                [ScopeName.PowerShellVariable]    = F("#9CDCFE"),
                [ScopeName.PowerShellOperator]    = F("#D4D4D4"),
                [ScopeName.PowerShellAttribute]   = F("#4EC9B0"),
                [ScopeName.CssPropertyName]       = F("#9CDCFE"),
                [ScopeName.CssPropertyValue]      = F("#CE9178"),
                [ScopeName.CssSelector]           = F("#D7BA7D"),
                [ScopeName.SqlSystemFunction]     = F("#DCDCAA"),
            };
        }
    }
}
