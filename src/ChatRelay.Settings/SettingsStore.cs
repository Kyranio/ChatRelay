using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatRelay.Logging;

namespace ChatRelay.Settings
{
    /// <summary>
    /// Global (user-level, not project-scoped) extension settings, persisted
    /// to <c>%LocalAppData%\ChatRelay\settings.json</c>. Loaded lazily on
    /// first access and cached; writes are atomic (tmp + replace).
    /// Subscribers hook <see cref="Changed"/> to react to saves — the settings
    /// window re-saves on OK, which fans out to the control and adapters so
    /// changes apply live.
    ///
    /// The DTO shapes (<see cref="ExtensionSettings"/>, <c>GeneralSettings</c>,
    /// etc.) live in <c>ChatRelay.Contracts</c> so the VSIX shell can build
    /// settings dialog payloads against the same types.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string StoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay",
            "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            // camelCase so the MCP section round-trips byte-compatibly with
            // standard .chatrelay.mcp.json (mcpServers, command, args, env, ...).
            // Case-insensitive read still accepts older PascalCase settings
            // files written by earlier versions.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static ExtensionSettings? _cache;
        private static readonly object _lock = new object();

        /// <summary>Raised after a successful save. UI thread is not guaranteed.</summary>
        public static event EventHandler? Changed;

        /// <summary>
        /// Returns the cached settings, loading from disk on first call.
        /// Any read failure (missing file, corrupt JSON) silently produces
        /// defaults — settings must never crash the tool window.
        /// </summary>
        public static ExtensionSettings Load()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(StoragePath))
                    {
                        var json = File.ReadAllText(StoragePath);
                        _cache = JsonSerializer.Deserialize<ExtensionSettings>(json, JsonOptions)
                                 ?? new ExtensionSettings();
                    }
                    else
                    {
                        _cache = new ExtensionSettings();
                    }
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("settings", "Load failed — falling back to defaults", ex);
                    _cache = new ExtensionSettings();
                }

                MigrateInPlace(_cache);
                return _cache;
            }
        }

        // One-shot migrations for users with settings files written by
        // earlier versions. Runs on every load but is idempotent —
        // a file that's already up-to-date sees no changes. The data
        // moves in-memory only; the next Save writes the current
        // (migrated) shape.
        private static void MigrateInPlace(ExtensionSettings s)
        {
            if (s?.General == null || s.Permissions == null) return;

            // AdditionalDirectories moved from General to Permissions. If
            // someone had paths in the old location and the new one is
            // still empty, hoist them over.
            var legacy = s.General.AdditionalDirectories;
            if (legacy != null && legacy.Count > 0
                && (s.Permissions.AdditionalDirectories == null
                    || s.Permissions.AdditionalDirectories.Count == 0))
            {
                s.Permissions.AdditionalDirectories = new List<string>(legacy);
                s.General.AdditionalDirectories = new List<string>();
                ExtensionLogger.Info("settings",
                    $"Migrated {legacy.Count} AdditionalDirectories entries from general → permissions");
            }
        }

        /// <summary>
        /// Persist the supplied settings, replace the in-memory cache, and
        /// raise <see cref="Changed"/>. Disk failures are swallowed (logged)
        /// so a write-locked file doesn't crash the UI.
        /// </summary>
        public static void Save(ExtensionSettings settings)
        {
            if (settings == null) return;

            lock (_lock)
            {
                _cache = settings;
                try
                {
                    var dir = Path.GetDirectoryName(StoragePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var tmp = StoragePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
                    if (File.Exists(StoragePath)) File.Delete(StoragePath);
                    File.Move(tmp, StoragePath);
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("settings", "Save failed", ex);
                }
            }

            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { ExtensionLogger.Warn("settings", "Changed handler threw", ex); }
        }
    }
}
