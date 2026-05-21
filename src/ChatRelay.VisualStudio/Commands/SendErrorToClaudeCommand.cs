using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChatRelay.Chat.Views;
using ChatRelay.Editor;

namespace ChatRelay.Commands;

/// <summary>
/// Tools menu → Send Selected Error(s) to ChatRelay. Pulls the currently
/// selected entries out of the Error List's WPF TableControl, formats each
/// as <c>"&lt;text&gt; — &lt;file&gt;:&lt;line&gt;"</c>, and pins them as
/// references in the chat input. User can then ask a follow-up about them.
///
/// Right-click integration on the Error List itself isn't VSCT-extensible
/// (the table uses a WPF-only context menu); a follow-up could add an
/// <c>ITableControlEventProcessorProvider</c> MEF export to surface this
/// command in that menu too. For now the keybinding (Ctrl+Alt+E) is the
/// fast path.
/// </summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.SendErrorToClaude)]
internal sealed class SendErrorToClaudeCommand : BaseCommand<SendErrorToClaudeCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var errorList = await VS.GetServiceAsync<SVsErrorList, IErrorList>();
        var entries = errorList?.TableControl?.SelectedEntries?.ToList();
        if (entries is null || entries.Count == 0) return;

        var solutionDir = EditorSelectionService.GetSolutionDirectory();

        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);
        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());

        foreach (var entry in entries)
        {
            var (displayPath, absolutePath, line, content) = FormatErrorEntry(entry, solutionDir);
            ChatWindow.AppendReferenceToWindow(displayPath, absolutePath, line, line, content);
        }
    }

    /// <summary>
    /// Extract a single entry's fields via the standard table-column keys
    /// and build the chip + reference body shape that
    /// <see cref="ChatWindow.AppendReferenceToWindow"/> expects.
    /// </summary>
    private static (string displayPath, string absolutePath, int line, string content)
        FormatErrorEntry(ITableEntryHandle entry, string? solutionDir)
    {
        entry.TryGetValue(StandardTableKeyNames.DocumentName, out var docObj);
        entry.TryGetValue(StandardTableKeyNames.Line, out var lineObj);
        entry.TryGetValue(StandardTableKeyNames.Text, out var textObj);
        entry.TryGetValue(StandardTableKeyNames.ProjectName, out var projObj);
        entry.TryGetValue(StandardTableKeyNames.ErrorCode, out var codeObj);
        entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out var severityObj);

        var absolutePath = docObj?.ToString() ?? string.Empty;
        // StandardTableKeyNames.Line is 0-based; we display 1-based.
        var line = lineObj is int n ? n : 0;
        var text = textObj?.ToString() ?? "(no description)";
        var project = projObj?.ToString();
        var code = codeObj?.ToString();
        var severity = severityObj?.ToString() ?? "error";

        var displayName = ShortenForChip(text);
        var displayPath = "@error: " + displayName;

        var body = new StringBuilder();
        body.Append(severity).Append(' ');
        if (!string.IsNullOrEmpty(code)) body.Append(code).Append(": ");
        body.AppendLine(text);
        if (!string.IsNullOrEmpty(absolutePath))
        {
            var rel = RelativeToSolution(absolutePath, solutionDir);
            body.Append("File: ").Append(rel).Append(':').Append(line + 1).AppendLine();
        }
        if (!string.IsNullOrEmpty(project)) body.Append("Project: ").AppendLine(project);

        return (displayPath, absolutePath, line, body.ToString().TrimEnd());
    }

    private static string ShortenForChip(string s)
    {
        const int max = 80;
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    private static string RelativeToSolution(string absolutePath, string? solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir)) return absolutePath;
        if (!absolutePath.StartsWith(solutionDir!, System.StringComparison.OrdinalIgnoreCase))
            return absolutePath;
        return absolutePath.Substring(solutionDir!.Length).TrimStart('\\', '/');
    }
}
