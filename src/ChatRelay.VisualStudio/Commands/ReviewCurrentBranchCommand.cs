using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ChatRelay.Chat.Views;
using ChatRelay.Editor;

namespace ChatRelay.Commands;

/// <summary>
/// Tools menu → Review Current Branch with ChatRelay. Pins the
/// <c>git diff &lt;base&gt;..HEAD</c> output as a reference and prefills the
/// input with a review prompt so a single Send fires the review off.
///
/// Base branch comes from <c>HostSettings.CodeReviewBaseBranch</c> when set,
/// otherwise auto-detects: main → master → develop → dev, whichever exists
/// in the local repo.
/// </summary>
[Command(PackageGuids.guidClaudeCmdSetString, PackageIds.ReviewCurrentBranch)]
internal sealed class ReviewCurrentBranchCommand : BaseCommand<ReviewCurrentBranchCommand>
{
    // Cap the diff we ship as a reference to keep the prompt within a
    // reasonable token budget. The model can still review huge branches —
    // we just truncate and include --stat so it knows what's missing.
    private const int MaxDiffChars = 200_000;

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var repoRoot = FindGitRoot(EditorSelectionService.GetSolutionDirectory());
        if (repoRoot is null)
        {
            await VS.MessageBox.ShowAsync(
                "ChatRelay — Review current branch",
                "No git repository found at or above the current solution directory.",
                OLEMSGICON.OLEMSGICON_INFO);
            return;
        }

        var baseBranch = ResolveBaseBranch(repoRoot);
        if (baseBranch is null)
        {
            await VS.MessageBox.ShowAsync(
                "ChatRelay — Review current branch",
                "Couldn't find a base branch to diff against. Tried main, master, develop, dev. " +
                "Future UI in settings will let you set CodeReviewBaseBranch; for now edit it directly in the settings JSON.",
                OLEMSGICON.OLEMSGICON_WARNING);
            return;
        }

        var currentBranch = RunGit(repoRoot, "rev-parse --abbrev-ref HEAD") ?? "HEAD";
        if (string.Equals(currentBranch, baseBranch, StringComparison.OrdinalIgnoreCase))
        {
            await VS.MessageBox.ShowAsync(
                "ChatRelay — Review current branch",
                $"You're on '{baseBranch}' — nothing to compare against itself.",
                OLEMSGICON.OLEMSGICON_INFO);
            return;
        }

        var stat = RunGit(repoRoot, $"diff --stat {baseBranch}..HEAD") ?? string.Empty;
        var commits = RunGit(repoRoot, $"log --oneline {baseBranch}..HEAD") ?? string.Empty;
        var diff = RunGit(repoRoot, $"diff {baseBranch}..HEAD") ?? string.Empty;

        if (stat.Length == 0 && diff.Length == 0)
        {
            await VS.MessageBox.ShowAsync(
                "ChatRelay — Review current branch",
                $"No changes between '{baseBranch}' and '{currentBranch}'.",
                OLEMSGICON.OLEMSGICON_INFO);
            return;
        }

        var truncated = diff.Length > MaxDiffChars;
        if (truncated) diff = diff.Substring(0, MaxDiffChars) + "\n\n[…diff truncated; full size " + diff.Length + " chars]";

        var content = new StringBuilder();
        content.Append("# Code review request\n\n");
        content.Append("Base branch: `").Append(baseBranch).Append("`\n");
        content.Append("Current branch: `").Append(currentBranch).Append("`\n\n");
        if (commits.Length > 0)
        {
            content.Append("## Commits\n\n```\n").Append(commits).Append("```\n\n");
        }
        if (stat.Length > 0)
        {
            content.Append("## Diffstat\n\n```\n").Append(stat).Append("```\n\n");
        }
        content.Append("## Full diff\n\n```diff\n").Append(diff).Append("\n```\n");

        var pane = await Package.FindToolWindowAsync(
            typeof(ChatWindow), id: 0, create: true, Package.DisposalToken);
        if (pane?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());

        ChatWindow.AppendReferenceToWindow(
            "@branch-diff: " + currentBranch + " vs " + baseBranch,
            absolutePath: Path.Combine(repoRoot, "(branch-diff)"),
            startLine: 0, endLine: 0,
            content: content.ToString());

        ChatWindow.SetInputTextOnWindow(
            $"Please review the changes on '{currentBranch}' compared to '{baseBranch}'. " +
            $"Call out bugs, regressions, style issues, missing tests, and anything that looks off.");
    }

    // Auto-detect today; configurable base branch from settings is a follow-up
    // (CodeReviewBaseBranch field is reserved on ExtensionSettings for that).
    private static string? ResolveBaseBranch(string repoRoot)
    {
        foreach (var candidate in new[] { "main", "master", "develop", "dev" })
            if (BranchExists(repoRoot, candidate)) return candidate;
        return null;
    }

    private static bool BranchExists(string repoRoot, string branch)
        => RunGit(repoRoot, $"rev-parse --verify --quiet {branch}") is { Length: > 0 };

    private static string? FindGitRoot(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir)) return null;
        var dir = new DirectoryInfo(startDir!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))   // worktree
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? RunGit(string cwd, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
