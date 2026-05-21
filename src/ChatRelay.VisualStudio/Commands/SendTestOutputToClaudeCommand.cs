using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading.Tasks;
using System.Windows;
using ChatRelay.Chat.Views;

namespace ChatRelay.Commands;

/// <summary>
/// Tools menu → Send Test Output to ChatRelay (Ctrl+Alt+T). Pulls
/// whatever's on the clipboard and pins it as an <c>@test-output</c>
/// reference in the chat. Intended workflow:
///
/// <list type="number">
///   <item>Run tests in VS, click a failing test in Test Explorer.</item>
///   <item>Right-click → Copy (built-in VS command — works for the
///   test name + error + stack).</item>
///   <item>Tools → Send Test Output to ChatRelay (or Ctrl+Alt+T).</item>
/// </list>
///
/// This is the universal-but-manual path. A future PR can plug into
/// <c>Microsoft.VisualStudio.TestWindow.Extensibility</c>'s
/// <c>IOperationState</c> to drive this directly from a Test Explorer
/// context-menu button without the clipboard hop.
/// </summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.SendTestOutputToClaude)]
internal sealed class SendTestOutputToClaudeCommand : BaseCommand<SendTestOutputToClaudeCommand>
{
    private const int MaxChars = 50_000;

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch { text = string.Empty; }

        if (string.IsNullOrWhiteSpace(text))
        {
            await VS.MessageBox.ShowAsync(
                "ChatRelay — Send Test Output",
                "Clipboard is empty. In Test Explorer, click a test result and choose Copy (or Ctrl+C) first, then run this command.",
                OLEMSGICON.OLEMSGICON_INFO);
            return;
        }

        // Cap at 50k chars — pasted test output can include enormous
        // diffs / dumps; the model only needs a representative slice.
        var truncated = text.Length > MaxChars;
        if (truncated) text = text.Substring(0, MaxChars) + "\n\n[…clipboard truncated; full size " + text.Length + " chars]";

        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);
        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());

        var summary = ShortenForChip(text);
        ChatWindow.AppendReferenceToWindow(
            "@test-output: " + summary,
            absolutePath: "(clipboard)",
            startLine: 0, endLine: 0,
            content: text);
    }

    private static string ShortenForChip(string s)
    {
        const int max = 60;
        var firstLine = s.Split('\n')[0].TrimEnd('\r');
        if (firstLine.Length <= max) return firstLine;
        return firstLine.Substring(0, max - 1) + "…";
    }
}
