using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatRelay.Logging;

namespace ChatRelay.Chat
{
    /// <summary>
    /// JSON-backed persistence for the chat sessions list. Scoped per
    /// solution so Project A's chats don't bleed into Project B — one file
    /// per solution path, stored under
    /// <c>%LocalAppData%\ChatRelay\sessions\&lt;hash&gt;.json</c>. When
    /// no solution is open we fall back to a shared "no solution" bucket.
    /// </summary>
    public static class SessionStore
    {
        private static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay",
            "sessions");

        // Legacy global file — migrated into the "no solution" bucket on
        // first access so no one loses their Chat 1 when they upgrade.
        private static readonly string LegacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay",
            "sessions.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Path to the sessions file for a given solution. Hashes the
        /// solution path so the filename is valid on disk regardless of
        /// weird chars; keeps the full path inside the JSON for debugging.
        /// </summary>
        private static string StoragePathFor(string? solutionPath)
        {
            var key = string.IsNullOrEmpty(solutionPath)
                ? "no-solution"
                : HashKey(solutionPath!);
            return Path.Combine(BaseDirectory, key + ".json");
        }

        private static string HashKey(string input)
        {
            // Canonicalise first so C:\foo\.\bar.sln, C:\FOO\BAR.sln, and a
            // mapped-drive alias all hash to the same bucket. GetFullPath can
            // throw on paths with invalid chars — fall back to the raw input
            // so we still land in *some* deterministic bucket.
            string canonical;
            try { canonical = Path.GetFullPath(input); }
            catch { canonical = input; }

            // SHA-1 is plenty — we're preventing filename collisions, not
            // defending against an adversary. Encoded as short hex.
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Current on-disk schema version. Bumped whenever persisted types
        /// change shape in a non-additive way. Files without a version field
        /// (pre-envelope v1) are loaded via the bare-array fallback.
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>Maximum persisted bubbles per session. Older ones are rolled up into a single truncation marker on save.</summary>
        public const int MaxPersistedMessages = 500;

        /// <summary>
        /// Load sessions for the given solution path. Null / empty means
        /// "no solution open" — we return the shared bucket for that.
        /// First call ever also upgrades the old global <c>sessions.json</c>
        /// into the no-solution bucket so pre-scoping chats aren't lost.
        /// </summary>
        public static List<PersistedSession> Load(string? solutionPath)
        {
            MigrateLegacyIfNeeded();

            var path = StoragePathFor(solutionPath);
            try
            {
                if (!File.Exists(path)) return new List<PersistedSession>();
                var json = File.ReadAllText(path);
                return Deserialize(json, path);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("sessions", "Load failed for " + path, ex);
                return new List<PersistedSession>();
            }
        }

        // Accepts both the legacy bare-array shape and the current
        // { schemaVersion, sessions } envelope. Unknown future versions log
        // and fall back to empty rather than attempting risky partial loads.
        private static List<PersistedSession> Deserialize(string json, string pathForDiagnostics)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<PersistedSession>();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                // v1: top-level is a raw array.
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var legacy = JsonSerializer.Deserialize<List<PersistedSession>>(root.GetRawText(), JsonOptions);
                    return legacy ?? new List<PersistedSession>();
                }

                // v2+: envelope with { schemaVersion, sessions }. Accept either
                // case since older files used PascalCase (the default C#
                // serialiser output) while newer ones write camelCase.
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (!root.TryGetProperty("schemaVersion", out var v))
                        root.TryGetProperty("SchemaVersion", out v);
                    var version = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
                    if (version > CurrentSchemaVersion)
                    {
                        ExtensionLogger.Warn("sessions",
                            $"Unsupported schemaVersion {version} in {pathForDiagnostics}; refusing to load.");
                        return new List<PersistedSession>();
                    }
                    if (!root.TryGetProperty("sessions", out var arr))
                        root.TryGetProperty("Sessions", out arr);
                    if (arr.ValueKind == JsonValueKind.Array)
                    {
                        var list = JsonSerializer.Deserialize<List<PersistedSession>>(arr.GetRawText(), JsonOptions);
                        return list ?? new List<PersistedSession>();
                    }
                }
            }
            return new List<PersistedSession>();
        }

        /// <summary>Persist sessions for the given solution. Atomic write. Preserves the existing <c>ActiveSessionId</c> across saves.</summary>
        public static void Save(string? solutionPath, List<PersistedSession> sessions)
        {
            var path = StoragePathFor(solutionPath);
            // Read existing ActiveSessionId so a plain Save doesn't wipe it.
            // Costs one extra disk read per save; acceptable for keeping the
            // API tiny.
            var existingActive = ReadActiveSessionId(path);
            WriteEnvelope(path, sessions, existingActive);
        }

        /// <summary>
        /// Returns the id of the session that was last opened in this
        /// workspace, or null if none has been recorded yet.
        /// </summary>
        public static string? GetActiveSession(string? solutionPath)
            => ReadActiveSessionId(StoragePathFor(solutionPath));

        /// <summary>
        /// Update just the <c>ActiveSessionId</c> for this workspace. Sessions
        /// are preserved as-is; pass null to clear.
        /// </summary>
        public static void SetActiveSession(string? solutionPath, string? activeSessionId)
        {
            var path = StoragePathFor(solutionPath);
            // Read sessions to preserve them across the rewrite.
            var sessions = File.Exists(path)
                ? Deserialize(SafeReadAll(path), path)
                : new List<PersistedSession>();
            WriteEnvelope(path, sessions, activeSessionId);
        }

        // Atomic envelope write. Single point of truth so Save and
        // SetActiveSession can't drift.
        private static void WriteEnvelope(string path, List<PersistedSession> sessions, string? activeSessionId)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);

                if (sessions != null) ClipOversizedMessages(sessions);

                var envelope = new PersistedEnvelope
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Sessions = sessions ?? new List<PersistedSession>(),
                    ActiveSessionId = activeSessionId,
                };

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("sessions", "Save failed for " + path, ex);
            }
        }

        private static string SafeReadAll(string path)
        {
            try { return File.ReadAllText(path); } catch { return string.Empty; }
        }

        // Pulls just the activeSessionId field out of an envelope without
        // re-deserialising the whole sessions list. Tolerates either case
        // because pre-camelCase persisted files might still exist.
        private static string? ReadActiveSessionId(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (doc.RootElement.TryGetProperty("activeSessionId", out var v)
                    && v.ValueKind == JsonValueKind.String) return v.GetString();
                if (doc.RootElement.TryGetProperty("ActiveSessionId", out v)
                    && v.ValueKind == JsonValueKind.String) return v.GetString();
                return null;
            }
            catch { return null; }
        }

        // Guard against unbounded growth: very long chats make every 2-second
        // debounced save rewrite a multi-MB file, and stateless adapters
        // replay the whole thing on every send. Trim older bubbles to a cap
        // and leave a single marker so users see that truncation happened.
        private static void ClipOversizedMessages(List<PersistedSession> sessions)
        {
            foreach (var s in sessions)
            {
                if (s?.Messages == null || s.Messages.Count <= MaxPersistedMessages) continue;
                var drop = s.Messages.Count - MaxPersistedMessages;
                s.Messages.RemoveRange(0, drop);
                s.Messages.Insert(0, new PersistedBubble
                {
                    Kind = BubbleKind.Error,
                    Label = "History",
                    Text = $"[Older history truncated — {drop} earlier message(s) dropped to keep this session file bounded.]",
                    Timestamp = DateTime.Now
                });
            }
        }

        // One-shot upgrade of %LocalAppData%\ChatRelay\sessions.json (the
        // pre-scoping location) into the shared "no solution" bucket, so a
        // user's existing chats survive the switch. Safe to call repeatedly —
        // becomes a no-op as soon as the legacy file is gone.
        private static bool _legacyMigrated;
        private static void MigrateLegacyIfNeeded()
        {
            if (_legacyMigrated) return;
            _legacyMigrated = true;

            try
            {
                if (!File.Exists(LegacyPath)) return;
                Directory.CreateDirectory(BaseDirectory);
                var target = StoragePathFor(null);
                if (!File.Exists(target))
                {
                    File.Move(LegacyPath, target);
                    ExtensionLogger.Info("sessions", "Migrated legacy sessions.json → " + target);
                }
                else
                {
                    // Someone already has a no-solution bucket; keep it and
                    // archive the legacy file rather than clobbering.
                    File.Move(LegacyPath, LegacyPath + ".migrated");
                    ExtensionLogger.Info("sessions", "Legacy sessions.json archived (bucket already existed)");
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Warn("sessions", "Legacy migration failed", ex);
            }
        }
    }

    /// <summary>
    /// On-disk envelope around the sessions list. The schemaVersion field
    /// lets future field renames / shape changes switch parsing strategies
    /// instead of silently returning an empty list (which would wipe chat
    /// history). Pre-envelope files are a bare JSON array and handled by
    /// <see cref="SessionStore.Deserialize"/>.
    /// </summary>
    public class PersistedEnvelope
    {
        public int SchemaVersion { get; set; } = SessionStore.CurrentSchemaVersion;
        public List<PersistedSession> Sessions { get; set; } = new List<PersistedSession>();

        /// <summary>
        /// Id of the session the user had open when they last interacted
        /// with this workspace. Used to restore selection on next launch
        /// instead of defaulting to the oldest session. Null until the
        /// host has tracked at least one openSession call.
        /// </summary>
        public string? ActiveSessionId { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BubbleKind
    {
        User,
        Assistant,
        Error
    }

    public class PersistedSession
    {
        public string Label { get; set; } = string.Empty;
        public string? SessionId { get; set; }

        /// <summary>Which adapter this session was using (claude-cli / claude-api / ollama). Null for pre-adapter sessions.</summary>
        public string? AdapterId { get; set; }

        /// <summary>Adapter-specific model id the user had selected on this session.</summary>
        public string? ModelId { get; set; }

        public List<PersistedBubble> Messages { get; set; } = new List<PersistedBubble>();
        public List<PersistedReference> References { get; set; } = new List<PersistedReference>();
        public string DraftText { get; set; } = string.Empty;
    }

    public class PersistedBubble
    {
        public BubbleKind Kind { get; set; }
        public string Label { get; set; } = string.Empty;   // Assistant: model name; Error: "Error"; User: unused
        public string Text { get; set; } = string.Empty;
        public List<PersistedReference> References { get; set; } = new List<PersistedReference>();

        /// <summary>Token/cost accounting, stamped onto the assistant bubble after its turn finishes. Null for user/error bubbles and for pre-usage-tracking saves.</summary>
        public PersistedUsage? Usage { get; set; }

        /// <summary>Local time the bubble was created. Nullable so legacy sessions saved before this field existed still load cleanly.</summary>
        public System.DateTime? Timestamp { get; set; }

        /// <summary>Extended-thinking / reasoning text shown in a collapsible expander on assistant bubbles. Null when the turn produced no thinking (or the backend doesn't report it).</summary>
        public string? Thinking { get; set; }

        /// <summary>
        /// Display name of the model that produced this assistant bubble
        /// (e.g. "Sonnet 4.5", "claude-opus-4-5"). Captured from the
        /// adapter's last <c>ModelInfo</c> event during the turn. Null
        /// for user / error bubbles and for assistant bubbles saved
        /// before this field existed.
        /// </summary>
        public string? Model { get; set; }
    }

    public class PersistedUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        public double? CostUsd { get; set; }
    }

    public class PersistedReference
    {
        public string FilePath { get; set; } = string.Empty;
        public string AbsolutePath { get; set; } = string.Empty;
        public List<PersistedRange> Ranges { get; set; } = new List<PersistedRange>();
        public string? FullContent { get; set; }
    }

    public class PersistedRange
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string? Body { get; set; }
    }
}
