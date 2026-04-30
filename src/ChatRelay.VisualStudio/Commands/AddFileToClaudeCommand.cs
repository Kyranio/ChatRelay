using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.IO;
using System.Threading.Tasks;
using ChatRelay.Chat.Views;
using ChatRelay.Editor;

namespace ChatRelay.Commands;

/// <summary>
/// Solution Explorer → right-click a file → Add to Claude Chat. Pins each
/// selected file as a reference (full contents included) and brings the pane
/// forward. Multi-select is supported; folders and projects don't see this
/// command because IDM_VS_CTXT_ITEMNODE only applies to file items.
/// </summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.AddFileToClaude)]
internal sealed class AddFileToClaudeCommand : BaseCommand<AddFileToClaudeCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // `Package` here refers to BaseCommand<T>.Package (AsyncPackage instance),
        // not the static Shell.Package class — fully qualify to reach GetGlobalService.
        var dte = (DTE2)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
        if (dte == null) return;

        var solutionDir = EditorSelectionService.GetSolutionDirectory();

        // Open / surface the chat pane so the pinned chips become visible.
        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);
        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());

        foreach (SelectedItem item in dte.SelectedItems)
        {
            var filePath = TryGetFilePath(item);
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) continue;

            var displayPath = "@" + RelativeToSolution(filePath!, solutionDir);

            string content;
            try { content = File.ReadAllText(filePath); }
            catch { content = string.Empty; }

            ChatWindow.AppendReferenceToWindow(
                displayPath, filePath!, startLine: 0, endLine: 0, content: content);
        }
    }

    private static string? TryGetFilePath(SelectedItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return item.ProjectItem?.FileNames[1]; } // 1-based per the DTE API
        catch { return null; }
    }

    private static string RelativeToSolution(string absolutePath, string? solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir)) return absolutePath;
        if (!absolutePath.StartsWith(solutionDir!, System.StringComparison.OrdinalIgnoreCase))
            return absolutePath;
        return absolutePath.Substring(solutionDir!.Length).TrimStart('\\', '/');
    }
}
