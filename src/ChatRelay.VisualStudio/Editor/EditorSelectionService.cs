using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;

namespace ChatRelay.Editor
{
    /// <summary>Reads the active editor's selection and formats it as an @file:line reference.</summary>
    public static class EditorSelectionService
    {
        public class Selection
        {
            public string FilePath { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public int StartLine { get; set; }
            public int EndLine { get; set; }

            /// <summary>Formats just the file portion as "@relative/path.cs" — line info is carried separately.</summary>
            public string AsClaudeFilePath(string? solutionDir = null)
            {
                var path = FilePath;
                if (!string.IsNullOrEmpty(solutionDir) && path.StartsWith(solutionDir!, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(solutionDir!.Length).TrimStart('\\', '/');
                }
                return "@" + path;
            }
        }

        public static Selection? GetCurrentSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            var doc = dte?.ActiveDocument;
            if (doc == null) return null;

            var selection = doc.Selection as TextSelection;
            if (selection == null) return null;

            // No selection → fall back to the current line.
            string text = selection.Text;
            int startLine = selection.TopPoint.Line;
            int endLine = selection.BottomPoint.Line;

            if (string.IsNullOrEmpty(text))
            {
                var line = selection.ActivePoint.CreateEditPoint();
                line.StartOfLine();
                var lineEnd = line.CreateEditPoint();
                lineEnd.EndOfLine();
                text = line.GetText(lineEnd);
                startLine = endLine = selection.ActivePoint.Line;
            }
            else if (IsWholeFileSelection(doc, startLine, endLine))
            {
                // Ctrl+A (or equivalent full-document selection) — emit as a
                // whole-file reference. AppendReference uses startLine ≤ 0 as
                // the "whole file" sentinel.
                startLine = 0;
                endLine = 0;
            }

            return new Selection
            {
                FilePath = doc.FullName,
                Text = text ?? string.Empty,
                StartLine = startLine,
                EndLine = endLine
            };
        }

        private static bool IsWholeFileSelection(Document doc, int startLine, int endLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (startLine != 1) return false;
            if (!(doc.Object("TextDocument") is TextDocument textDoc)) return false;
            // EndPoint.Line sometimes reads one past the last content line if the
            // file ends with a newline — the >= handles that.
            return endLine >= textDoc.EndPoint.Line;
        }

        public static string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            var solution = dte?.Solution;
            if (solution == null || string.IsNullOrEmpty(solution.FullName)) return null;
            return System.IO.Path.GetDirectoryName(solution.FullName);
        }

        /// <summary>
        /// Absolute path of the currently-open solution file (.sln), or null
        /// when no solution is loaded. Used by SessionStore to scope chat
        /// history per project.
        /// </summary>
        public static string? GetSolutionPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            var solution = dte?.Solution;
            if (solution == null || string.IsNullOrEmpty(solution.FullName)) return null;
            return solution.FullName;
        }

        /// <summary>Absolute path of the document currently focused in the editor, or null.</summary>
        public static string? GetActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            return dte?.ActiveDocument?.FullName;
        }

        /// <summary>Converts an absolute path to the "@relative/path.cs" display form used by reference chips.</summary>
        public static string? MakeClaudeFilePath(string? absolutePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(absolutePath)) return null;
            var solutionDir = GetSolutionDirectory();
            if (!string.IsNullOrEmpty(solutionDir)
                && absolutePath!.StartsWith(solutionDir!, StringComparison.OrdinalIgnoreCase))
            {
                return "@" + absolutePath.Substring(solutionDir!.Length).TrimStart('\\', '/');
            }
            return "@" + absolutePath;
        }

        /// <summary>
        /// Opens <paramref name="absolutePath"/> in the editor and, if
        /// <paramref name="startLine"/> is set, selects from it through
        /// <paramref name="endLine"/>. Must be called on the UI thread.
        /// </summary>
        public static void Navigate(string absolutePath, int startLine, int endLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(absolutePath)) return;
            try
            {
                var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
                if (dte == null) return;

                dte.ItemOperations.OpenFile(absolutePath);

                if (startLine > 0 && dte.ActiveDocument?.Selection is TextSelection sel)
                {
                    sel.GotoLine(startLine);
                    if (endLine >= startLine)
                    {
                        sel.MoveToLineAndOffset(endLine, 1, true);
                        sel.EndOfLine(true);
                    }
                }
            }
            catch { /* file moved, permissions, etc. — swallow */ }
        }
    }
}
