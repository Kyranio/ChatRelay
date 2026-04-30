using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;
using ChatRelay.Chat.Views;

namespace ChatRelay.Commands;

/// <summary>Tools → Open ChatRelay: creates (if needed) and shows the tool window.</summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.OpenChatWindow)]
internal sealed class OpenChatWindowCommand : BaseCommand<OpenChatWindowCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());
    }
}
