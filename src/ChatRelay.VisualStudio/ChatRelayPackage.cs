using System;
using System.Runtime.InteropServices;
using System.Threading;
using ChatRelay.Chat.Views;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ChatRelay;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("ChatRelay for Visual Studio", "Multi-backend AI chat for VS", "0.1")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ChatWindow))]
[Guid(PackageGuidString)]
public sealed class ChatRelayPackage : AsyncPackage
{
    public const string PackageGuidString = "d4f5a1b2-1234-5678-9abc-def012345678";

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        await Commands.OpenChatWindowCommand.InitializeAsync(this);
        await Commands.SendSelectionCommand.InitializeAsync(this);
        await Commands.AddFileToClaudeCommand.InitializeAsync(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ChatWindow.Control?.DisposeHost();
        base.Dispose(disposing);
    }

    protected override int QueryClose(out bool canClose)
    {
        canClose = ChatWindow.Control?.ConfirmCloseWithPendingChanges() ?? true;
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }
}
