using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatRelay.Permissions
{
    /// <summary>
    /// Decides whether a permission request targets paths inside the
    /// current workspace. When it doesn't, also picks the "lowest point"
    /// parent folder of the external path — that's the granularity at
    /// which session-scoped allows are remembered.
    ///
    /// Heuristics:
    /// <list type="bullet">
    ///   <item>Tools with <c>file_path</c> / <c>filePath</c> / <c>path</c>:
    ///   use that value directly.</item>
    ///   <item>Tools with a <c>command</c> field (Bash etc.): scan the
    ///   command string for absolute / drive-rooted path tokens.</item>
    ///   <item>Anything else: treated as in-workspace.</item>
    /// </list>
    /// </summary>
    public static class PermissionScope
    {
        // Catches /abc/def, ~/foo, C:\foo, D:/foo style tokens. Stops at
        // whitespace or quotes. Greedy enough for most real Bash commands;
        // good-enough rather than a full shell parser.
        private static readonly Regex PathLike = new Regex(
            @"(?:[A-Za-z]:[\\/]|/|~/)[^\s'""]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Returns true when every path the request mentions sits under
        /// <paramref name="workspaceRoot"/>. When false, <paramref name="externalFolder"/>
        /// is the lowest-point parent folder of the first external path
        /// (suitable for storing a session-scoped allow rule); empty
        /// when no path could be extracted.
        /// </summary>
        public static bool IsInWorkspace(string inputJson, string? workspaceRoot, out string externalFolder)
        {
            externalFolder = string.Empty;

            // No workspace → no in-workspace concept; any extracted path is
            // treated as external and scoped exactly to itself.
            var workspaceCanonical = string.IsNullOrEmpty(workspaceRoot)
                ? null
                : Canonicalise(workspaceRoot!);

            foreach (var path in ExtractPaths(inputJson))
            {
                var resolved = Canonicalise(path, workspaceRoot);
                if (resolved is null) continue;

                if (workspaceCanonical is null || !IsUnder(resolved, workspaceCanonical))
                {
                    // Scope to the exact resolved path. Reducing to a parent
                    // (Directory.Exists ? path : GetDirectoryName) collapsed
                    // sibling folders like C:\temp and C:\otherfolder to the
                    // drive root when neither existed on disk, which made a
                    // session-allow for one auto-allow the other.
                    externalFolder = resolved;
                    return false;
                }
            }

            // No path could be extracted. With a workspace, default to
            // in-workspace (the broker bubble's 4-button layout is fine for
            // pathless tools like `pwd`). Without one, default to external
            // with an empty folder — the session-allow button labels itself
            // "for this session" in that case.
            return workspaceCanonical is not null;
        }

        private static System.Collections.Generic.IEnumerable<string> ExtractPaths(string inputJson)
        {
            if (string.IsNullOrEmpty(inputJson)) yield break;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                root = doc.RootElement.Clone();
            }
            catch
            {
                yield break;
            }

            foreach (var key in new[] { "file_path", "filePath", "path", "directory", "cwd" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                }
            }

            if (root.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            {
                var s = cmd.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    foreach (Match m in PathLike.Matches(s!))
                        yield return m.Value;
                }
            }
        }

        // Canonicalise relative to a base (when supplied). Returns null on
        // any path-shape failure — callers skip those.
        private static string? Canonicalise(string path, string? baseDir = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (path.StartsWith("~"))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = Path.Combine(home, path.Substring(1).TrimStart('/', '\\'));
                }
                var full = !string.IsNullOrEmpty(baseDir) && !Path.IsPathRooted(path)
                    ? Path.GetFullPath(Path.Combine(baseDir!, path))
                    : Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        // Case-insensitive on Windows, trailing-slash tolerant.
        private static bool IsUnder(string child, string parent)
        {
            var c = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return c.Equals(p, StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
