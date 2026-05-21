using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatRelay.Logging;

namespace ChatRelay.Permissions
{
    /// <summary>
    /// JSON-backed storage for "always allow" permission rules.
    ///
    /// Two on-disk tiers:
    /// <list type="bullet">
    ///   <item><b>Global</b> — <c>%LocalAppData%\ChatRelay\permissions-global.json</c>.
    ///   Applies in every workspace.</item>
    ///   <item><b>Per-workspace</b> — <c>%LocalAppData%\ChatRelay\permissions\&lt;hash&gt;.json</c>,
    ///   hashed by canonicalised workspace path. Only applies inside that
    ///   workspace.</item>
    /// </list>
    ///
    /// Session-scoped rules live in-memory on the host (see
    /// <see cref="SessionRules"/>) — they don't survive a VS restart.
    ///
    /// A "rule" is a tool name. Future extensions can add per-argument
    /// granularity; today it's tool-name-only because that matches the
    /// permission broker's level of detail.
    /// </summary>
    public static class PermissionRulesStore
    {
        private static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay");

        private static readonly string GlobalPath = Path.Combine(BaseDirectory, "permissions-global.json");
        private static readonly string WorkspaceBaseDirectory = Path.Combine(BaseDirectory, "permissions");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static HashSet<string> LoadGlobal() => LoadFile(GlobalPath);

        public static void AddGlobal(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            var set = LoadGlobal();
            if (set.Add(toolName)) SaveFile(GlobalPath, set);
        }

        /// <summary>Drop a previously-stored global allow. No-op when the rule isn't there.</summary>
        public static void RemoveGlobal(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            var set = LoadGlobal();
            if (set.Remove(toolName)) SaveFile(GlobalPath, set);
        }

        public static HashSet<string> LoadWorkspace(string? workspacePath)
            => LoadFile(WorkspacePathFor(workspacePath));

        public static void AddWorkspace(string? workspacePath, string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            var path = WorkspacePathFor(workspacePath);
            var set = LoadFile(path);
            if (set.Add(toolName)) SaveFile(path, set);
        }

        /// <summary>Drop a previously-stored workspace allow. No-op when the rule isn't there.</summary>
        public static void RemoveWorkspace(string? workspacePath, string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return;
            var path = WorkspacePathFor(workspacePath);
            var set = LoadFile(path);
            if (set.Remove(toolName)) SaveFile(path, set);
        }

        private static string WorkspacePathFor(string? workspacePath)
        {
            var key = string.IsNullOrEmpty(workspacePath)
                ? "no-workspace"
                : HashKey(workspacePath!);
            return Path.Combine(WorkspaceBaseDirectory, key + ".json");
        }

        private static string HashKey(string input)
        {
            string canonical;
            try { canonical = Path.GetFullPath(input); }
            catch { canonical = input; }

            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static HashSet<string> LoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<RulesFile>(json, JsonOptions);
                return new HashSet<string>(dto?.Tools ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("permissions", "Load failed for " + path, ex);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveFile(string path, HashSet<string> tools)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var dto = new RulesFile { Tools = tools.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList() };
                File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("permissions", "Save failed for " + path, ex);
            }
        }

        private class RulesFile
        {
            public List<string> Tools { get; set; } = new();
        }
    }

    /// <summary>
    /// In-memory session-scoped "always allow" rules. Keyed by sessionId,
    /// each session has a set of <c>(toolName, externalFolder)</c> pairs.
    /// Cleared when the host process exits.
    /// </summary>
    public sealed class SessionRules
    {
        // sessionId → set of "toolName|folder" keys.
        private readonly Dictionary<string, HashSet<string>> _rules = new();
        private readonly object _lock = new();

        public bool IsAllowed(string sessionId, string toolName, string externalFolder)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolName)) return false;
            lock (_lock)
            {
                return _rules.TryGetValue(sessionId, out var set)
                       && set.Contains(MakeKey(toolName, externalFolder));
            }
        }

        public void Add(string sessionId, string toolName, string externalFolder)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolName)) return;
            lock (_lock)
            {
                if (!_rules.TryGetValue(sessionId, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _rules[sessionId] = set;
                }
                set.Add(MakeKey(toolName, externalFolder));
            }
        }

        /// <summary>Drop a previously-stored session allow. No-op when the rule isn't there.</summary>
        public void Remove(string sessionId, string toolName, string externalFolder)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolName)) return;
            lock (_lock)
            {
                if (_rules.TryGetValue(sessionId, out var set))
                    set.Remove(MakeKey(toolName, externalFolder));
            }
        }

        private static string MakeKey(string toolName, string folder)
            => toolName + "|" + (folder ?? string.Empty);
    }
}
