using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChatRelay.Settings;
using ChatRelay.Logging;
using ChatRelay.Paths;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Helpers for mutating and reading <see cref="ExtensionSettings.McpFiles"/>.
    /// Operates on the current <see cref="SettingsStore"/> cache and persists
    /// through <see cref="SettingsStore.Save"/> so any consumer can pick up
    /// changes via the <see cref="SettingsStore.Changed"/> event.
    ///
    /// The registry is the authoritative list of MCP config files the
    /// extension knows about. Anything not in this list is invisible to
    /// the send-time merge and to the Settings window's MCP tab.
    /// </summary>
    public static class McpFileRegistry
    {
        /// <summary>
        /// Returns every tracked file. Pure read — does not seed defaults.
        /// Call <see cref="EnsureSeeded"/> first if you want the first-run
        /// auto-population.
        /// </summary>
        public static IReadOnlyList<TrackedMcpFile> All()
        {
            var list = SettingsStore.Load().McpFiles ?? new List<TrackedMcpFile>();
            return list.ToList();
        }

        /// <summary>
        /// Returns tracked files that apply to the given solution path:
        /// every <see cref="McpFileScope.Global"/> file, plus any
        /// <see cref="McpFileScope.Project"/> file whose
        /// <see cref="TrackedMcpFile.ScopedSolutionPath"/> matches
        /// <paramref name="solutionPath"/>.
        /// </summary>
        public static IReadOnlyList<TrackedMcpFile> ApplicableFor(string? solutionPath)
        {
            var list = All();
            var result = new List<TrackedMcpFile>();
            foreach (var f in list)
            {
                if (f.Scope == McpFileScope.Global)
                {
                    result.Add(f);
                }
                else if (f.Scope == McpFileScope.Project
                    && !string.IsNullOrEmpty(solutionPath)
                    && PathsEqual(f.ScopedSolutionPath, solutionPath))
                {
                    result.Add(f);
                }
            }
            return result;
        }

        /// <summary>
        /// Idempotent. Adds <paramref name="filePath"/> to the tracked list
        /// if not already present (comparison is by absolute path, case-
        /// insensitive). Returns true if the list changed.
        /// </summary>
        public static bool Track(string filePath, McpFileScope scope, string? scopedSolutionPath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            var abs = AbsOrOriginal(filePath);
            var current = SettingsStore.Load();
            var list = current.McpFiles ?? new List<TrackedMcpFile>();

            if (list.Any(f => PathsEqual(f.FilePath, abs)
                           && f.Scope == scope
                           && PathsEqual(f.ScopedSolutionPath, scopedSolutionPath)))
            {
                return false;
            }

            list.Add(new TrackedMcpFile
            {
                FilePath = abs,
                Scope = scope,
                ScopedSolutionPath = scope == McpFileScope.Project ? scopedSolutionPath : null
            });

            Save(current, list);
            ExtensionLogger.Info("mcp-files",
                $"Tracked {scope} file: {abs}"
                + (scope == McpFileScope.Project ? $" (scoped to {scopedSolutionPath})" : ""));
            return true;
        }

        /// <summary>
        /// Removes the entry from the list. The actual file on disk is
        /// left alone — users delete files explicitly, we just stop
        /// tracking them.
        /// </summary>
        public static bool Untrack(TrackedMcpFile entry)
        {
            if (entry == null) return false;

            var current = SettingsStore.Load();
            var list = current.McpFiles ?? new List<TrackedMcpFile>();
            var before = list.Count;

            list.RemoveAll(f =>
                PathsEqual(f.FilePath, entry.FilePath)
                && f.Scope == entry.Scope
                && PathsEqual(f.ScopedSolutionPath, entry.ScopedSolutionPath));

            if (list.Count == before) return false;

            Save(current, list);
            ExtensionLogger.Info("mcp-files", "Untracked: " + entry.FilePath);
            return true;
        }

        /// <summary>
        /// First-run seeding. If the tracked list is empty, add the two
        /// well-known locations (user-global + detected project) *when
        /// the files actually exist on disk* so existing users upgrading
        /// from the pre-registry version don't lose state. Brand-new
        /// users with no files get an empty list — the "Add configuration
        /// file…" buttons in the empty state handle creation.
        /// Safe to call on every open — no-op when the list is non-empty.
        /// </summary>
        public static void EnsureSeeded(string? solutionPath)
        {
            var current = SettingsStore.Load();
            if (current.McpFiles != null && current.McpFiles.Count > 0) return;

            var list = new List<TrackedMcpFile>();

            if (File.Exists(McpConfigService.GlobalConfigPath))
            {
                list.Add(new TrackedMcpFile
                {
                    FilePath = McpConfigService.GlobalConfigPath,
                    Scope = McpFileScope.Global
                });
            }

            if (!string.IsNullOrEmpty(solutionPath))
            {
                var solutionDir = Path.GetDirectoryName(solutionPath);
                var projectPath = McpConfigService.GetProjectConfigPath(solutionDir);
                if (!string.IsNullOrEmpty(projectPath) && File.Exists(projectPath))
                {
                    list.Add(new TrackedMcpFile
                    {
                        FilePath = projectPath!,
                        Scope = McpFileScope.Project,
                        ScopedSolutionPath = solutionPath
                    });
                }
            }

            if (list.Count == 0) return; // nothing to seed — leave state untouched

            Save(current, list);
            ExtensionLogger.Info("mcp-files", $"Auto-seeded {list.Count} entry(ies) on first open");
        }

        /// <summary>
        /// Drop tracked entries whose files no longer exist on disk. Called
        /// whenever the settings window refreshes so an external delete
        /// (Explorer, git checkout, rm -rf, …) cleans out of the registry
        /// by the next time the user looks. Returns the number of entries
        /// removed.
        /// </summary>
        public static int PruneMissing()
        {
            var current = SettingsStore.Load();
            var list = current.McpFiles ?? new List<TrackedMcpFile>();
            if (list.Count == 0) return 0;

            var kept = list
                .Where(f => !string.IsNullOrEmpty(f.FilePath) && File.Exists(f.FilePath))
                .ToList();

            var removed = list.Count - kept.Count;
            if (removed == 0) return 0;

            Save(current, kept);
            ExtensionLogger.Info("mcp-files",
                $"Pruned {removed} tracked file(s) whose path no longer exists on disk");
            return removed;
        }

        private static void Save(ExtensionSettings current, List<TrackedMcpFile> newList)
        {
            current.McpFiles = newList;
            SettingsStore.Save(current);
        }

        private static string AbsOrOriginal(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static bool PathsEqual(string? a, string? b) => PathHelper.Equals(a, b);
    }
}
