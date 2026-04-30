using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;
using ChatRelay.Chat.Views;
using ChatRelay.Editor;

namespace ChatRelay.Commands;

/// <summary>Editor context → Send Selection to Claude: pushes an @file:line reference into the chat.</summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.SendSelection)]
internal sealed class SendSelectionCommand : BaseCommand<SendSelectionCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var selection = EditorSelectionService.GetCurrentSelection();
        if (selection is null) return;

        var solutionDir = EditorSelectionService.GetSolutionDirectory();
        var displayPath = selection.AsClaudeFilePath(solutionDir);

        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);

        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());

        ChatWindow.AppendReferenceToWindow(
            displayPath, selection.FilePath,
            selection.StartLine, selection.EndLine, selection.Text);
    }
}
