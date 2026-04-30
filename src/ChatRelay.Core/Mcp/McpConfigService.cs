using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatRelay.Settings;
using ChatRelay.Logging;
using ChatRelay.Paths;

namespace ChatRelay.Mcp
{
    /// <summary>
    /// Handles MCP (Model Context Protocol) server configs. Merges the
    /// user's global config with any project-local
    /// <c>.chatrelay.mcp.json</c> at the solution root, and writes the
    /// combined result to a temp file handed to the Claude CLI via
    /// <c>--mcp-config</c>.
    ///
    /// Project settings win over global on name conflict — the local file
    /// is the more specific override.
    ///
    /// File format is <c>{ "mcpServers": { "name": { ... } } }</c> — same
    /// root key as Claude Code's convention, but written to a distinct
    /// <c>.chatrelay.mcp.json</c> filename so it doesn't collide with
    /// VS Copilot's <c>.mcp.json</c> (which uses a different schema).
    /// Keeping the filenames disjoint means VS never tries to apply its
    /// own override / validation logic to our files.
    /// </summary>
    public static class McpConfigService
    {
        private const string ProjectFileName = ".chatrelay.mcp.json";

        /// <summary>
        /// Global (user-scoped) MCP config path —
        /// <c>%LocalAppData%\ChatRelay\.chatrelay.mcp.json</c>. Sibling
        /// of settings.json, standalone so it can be edited directly in
        /// VS and pointed at by hyperlinks in the settings window.
        /// </summary>
        public static string GlobalConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay",
            ProjectFileName);

        /// <summary>Config-file key used for the permission-prompt broker.</summary>
        public const string PermissionBrokerServerName = "cvs-permissions";

        /// <summary>
        /// MCP tool id the CLI calls for permission prompts, formatted the
        /// way <c>--permission-prompt-tool</c> expects:
        /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.
        /// </summary>
        public const string PermissionPromptToolId = "mcp__" + PermissionBrokerServerName + "__approve";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };


        /// <summary>
        /// Reads and parses an explicit file path. Expects the ChatRelay
        /// <c>.chatrelay.mcp.json</c> format — <c>mcpServers</c> root,
        /// entries matching the <see cref="McpServerEntry"/> shape. Any
        /// unknown root keys are ignored silently; a malformed file just
        /// returns null (settings + registry handle that defensively).
        /// </summary>
        public static McpConfig? TryLoadFile(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return ParseJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp", "Failed to read " + path, ex);
                return null;
            }
        }

        // Raw-JSON parse so we can back-fill an implicit type on each
        // entry (command → stdio, url → http) without requiring the
        // author to spell it out. Anything outside <c>mcpServers</c> is
        // ignored — the file is ours, and we don't promise to preserve
        // keys we don't understand.
        private static McpConfig? ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new McpConfig();

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new McpConfig();

                var result = new McpConfig();

                if (!root.TryGetProperty("mcpServers", out var serversEl)
                    || serversEl.ValueKind != JsonValueKind.Object)
                {
                    return result;
                }

                foreach (var prop in serversEl.EnumerateObject())
                {
                    var entry = JsonSerializer.Deserialize<McpServerEntry>(
                        prop.Value.GetRawText(), JsonOptions);
                    if (entry == null) continue;

                    // Back-fill a default type so downstream code doesn't
                    // have to re-detect.
                    if (string.IsNullOrEmpty(entry.Type))
                    {
                        if (!string.IsNullOrEmpty(entry.Command)) entry.Type = "stdio";
                        else if (!string.IsNullOrEmpty(entry.Url)) entry.Type = "http";
                    }

                    result.McpServers[prop.Name] = entry;
                }

                return result;
            }
        }

        /// <summary>
        /// Empty scaffold written when the settings window creates a new
        /// <c>.chatrelay.mcp.json</c>. The user fills in the servers
        /// object with their own entries.
        /// </summary>
        public const string EmptyScaffold =
            "{\n" +
            "  \"mcpServers\": {\n" +
            "  }\n" +
            "}\n";

        /// <summary>
        /// Returns the absolute path to the project-scoped <c>.chatrelay.mcp.json</c>.
        /// Resolution:
        ///   1. If the solution lives in a git repo AND the repo root has a
        ///      <c>.chatrelay.mcp.json</c>, return that. Convention: configs live at
        ///      repo root.
        ///   2. Else return the solution directory path — checked for an
        ///      existing file first, and if none exists that's where
        ///      <c>CreateEmptyMcpConfigIfMissing</c> will place the new one.
        /// No walking between those two points — either git-root or
        /// solution-dir, nothing in between.
        /// </summary>
        public static string? GetProjectConfigPath(string? solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir)) return null;

            var gitRoot = FindRepoRoot(solutionDir!);
            if (gitRoot != null)
            {
                var atGitRoot = Path.Combine(gitRoot, ProjectFileName);
                if (File.Exists(atGitRoot)) return atGitRoot;
            }

            return Path.Combine(solutionDir!, ProjectFileName);
        }

        // Best-effort: find a .sln / .slnx file in the supplied directory.
        // Used by the tracked-registry iteration to match a project-scoped
        // entry's ScopedSolutionPath when we only have the directory in
        // hand. Returns null when no solution file is present.
        internal static string? GuessSolutionPathFromDir(string? solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return null;
            try
            {
                return Directory.EnumerateFiles(solutionDir, "*.sln").FirstOrDefault()
                    ?? Directory.EnumerateFiles(solutionDir, "*.slnx").FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // Walks up from startDir looking for a ".git" directory, which marks
        // the top of a git repository. Returns null when the project isn't
        // under git control — caller falls back to the solution directory.
        private static string? FindRepoRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp", "FindRepoRoot failed", ex);
            }
            return null;
        }

        /// <summary>
        /// Build the merged config from the current settings + project
        /// <c>.chatrelay.mcp.json</c> and write it to a fresh temp file the CLI can
        /// read via <c>--mcp-config</c>. Returns the file path, or null if
        /// there are no servers to pass through. Caller owns cleanup — the
        /// file lives in <c>%TEMP%</c> and the OS will eventually reclaim
        /// it if something forgets.
        ///
        /// When <paramref name="brokerExePath"/> and <paramref name="brokerPipeName"/>
        /// are both set, injects our permission-prompt MCP server into the
        /// merged config so the CLI can call back into the extension for
        /// approve/deny. Other adapters leave both null and get the plain
        /// user-configured servers.
        /// </summary>
        public static string? WriteMergedTempFile(string? solutionDir,
                                                 string? brokerExePath = null,
                                                 string? brokerPipeName = null)
        {
            try
            {
                // Iterate every tracked file that applies to the current
                // solution (all globals + project files scoped to this
                // solution). Later-scoped entries override earlier ones on
                // name conflict — project beats global because we sort
                // globals first.
                var solutionPath = string.IsNullOrEmpty(solutionDir)
                    ? null
                    : GuessSolutionPathFromDir(solutionDir);

                var applicable = McpFileRegistry.ApplicableFor(solutionPath)
                    .OrderBy(f => f.Scope == McpFileScope.Global ? 0 : 1)
                    .ToList();

                McpConfig merged = new McpConfig();
                foreach (var tracked in applicable)
                {
                    var parsed = TryLoadFile(tracked.FilePath);
                    if (parsed?.McpServers == null) continue;
                    foreach (var kv in parsed.McpServers)
                        merged.McpServers[kv.Key] = kv.Value;
                }

                // Respect user-explicit stops: any server the user has
                // stopped via the settings window should also be withheld
                // from the Claude CLI's --mcp-config so the CLI doesn't
                // spawn its own copy and silently re-expose the tools.
                // Match by name against the shared runtime's live handles.
                var stoppedByUser = McpRuntimeHost.Instance.Servers
                    .Where(h => h.UserStopped)
                    .Select(h => h.Name)
                    .ToList();
                foreach (var name in stoppedByUser)
                {
                    if (merged.McpServers.Remove(name))
                        ExtensionLogger.Info("mcp",
                            "Excluding user-stopped server from CLI merge: " + name);
                }

                // Inject the broker unconditionally (when requested) — even
                // when the user has no global/project MCP servers, we still
                // need it wired for --permission-prompt-tool to resolve.
                if (!string.IsNullOrEmpty(brokerExePath) && !string.IsNullOrEmpty(brokerPipeName))
                {
                    merged.McpServers[PermissionBrokerServerName] = new McpServerEntry
                    {
                        Command = brokerExePath,
                        Env = new Dictionary<string, string>
                        {
                            ["CLAUDEVS_PIPE"] = brokerPipeName!
                        }
                    };
                }

                if (merged.McpServers.Count == 0) return null;

                var tempPath = Path.Combine(Path.GetTempPath(),
                    $"chatrelay-mcp-{Guid.NewGuid():N}.json");
                File.WriteAllText(tempPath, Serialize(merged), Encoding.UTF8);
                return tempPath;
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("mcp", "Failed to assemble merged MCP config", ex);
                return null;
            }
        }

        /// <summary>
        /// Serialise an <see cref="McpConfig"/> to the ChatRelay
        /// <c>.chatrelay.mcp.json</c> format: <c>mcpServers</c> root,
        /// one entry per configured server. Also used verbatim for the
        /// temp file handed to <c>claude --mcp-config</c> — the Claude
        /// CLI accepts the same root key, so no translation step is
        /// required.
        /// </summary>
        public static string Serialize(McpConfig? config)
        {
            var root = config ?? new McpConfig();
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WritePropertyName("mcpServers");
                    w.WriteStartObject();
                    foreach (var kv in root.McpServers)
                    {
                        w.WritePropertyName(kv.Key);
                        w.WriteStartObject();
                        if (!string.IsNullOrEmpty(kv.Value.Type))
                            w.WriteString("type", kv.Value.Type);
                        if (!string.IsNullOrEmpty(kv.Value.Command))
                            w.WriteString("command", kv.Value.Command);
                        if (kv.Value.Args != null && kv.Value.Args.Count > 0)
                        {
                            w.WritePropertyName("args");
                            w.WriteStartArray();
                            foreach (var a in kv.Value.Args) w.WriteStringValue(a ?? string.Empty);
                            w.WriteEndArray();
                        }
                        if (kv.Value.Env != null && kv.Value.Env.Count > 0)
                        {
                            w.WritePropertyName("env");
                            w.WriteStartObject();
                            foreach (var kve in kv.Value.Env) w.WriteString(kve.Key, kve.Value ?? string.Empty);
                            w.WriteEndObject();
                        }
                        if (!string.IsNullOrEmpty(kv.Value.Url))
                            w.WriteString("url", kv.Value.Url);
                        if (kv.Value.Headers != null && kv.Value.Headers.Count > 0)
                        {
                            w.WritePropertyName("headers");
                            w.WriteStartObject();
                            foreach (var kvh in kv.Value.Headers) w.WriteString(kvh.Key, kvh.Value ?? string.Empty);
                            w.WriteEndObject();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>
        /// Alias retained for the Settings UI's read-only JSON preview.
        /// Forwards to <see cref="Serialize"/>.
        /// </summary>
        public static string ToJson(McpConfig? config) => Serialize(config);

        /// <summary>
        /// Parse user-pasted JSON into an <see cref="McpConfig"/>.
        /// Expects the ChatRelay <c>mcpServers</c> root key. Returns null
        /// and populates <paramref name="error"/> when invalid, so the
        /// settings window can surface the problem without crashing.
        /// </summary>
        public static McpConfig? TryParseJson(string? json, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
                return new McpConfig();

            try
            {
                return ParseJson(json!) ?? new McpConfig();
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
