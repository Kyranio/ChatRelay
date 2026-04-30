using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatRelay.Logging
{
    /// <summary>
    /// Fire-and-forget file logger for anything that isn't surfaced in the UI
    /// (adapter probe results, CLI/API request round-trips, persistence hiccups).
    /// One file per day under %LocalAppData%\ChatRelay\logs\. Writes are
    /// serialised through an async-friendly lock so concurrent callers don't
    /// interleave lines; everything swallows its own exceptions because logging
    /// must never bring down the tool window.
    /// </summary>
    public static class ExtensionLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRelay",
            "logs");

        private static readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);

        public enum Level { Debug, Info, Warn, Error }

        /// <summary>
        /// When false (default), <see cref="Debug"/> calls are discarded. Flip
        /// in code when diagnosing — no UI for this today; it's a dev toggle.
        /// </summary>
        public static bool VerboseLogging { get; set; } = false;

        public static void Debug(string source, string message) => Write(Level.Debug, source, message, null);
        public static void Info(string source, string message) => Write(Level.Info, source, message, null);
        public static void Warn(string source, string message) => Write(Level.Warn, source, message, null);
        public static void Warn(string source, string message, Exception ex) => Write(Level.Warn, source, message, ex);
        public static void Error(string source, string message) => Write(Level.Error, source, message, null);
        public static void Error(string source, string message, Exception ex) => Write(Level.Error, source, message, ex);

        private static void Write(Level level, string source, string message, Exception? ex)
        {
            // Debug is gated by the verbose flag so "dropped to Debug" actually
            // means "silent by default" rather than "labelled differently".
            if (level == Level.Debug && !VerboseLogging) return;

            // Don't block the caller — dump to disk on a background thread.
            _ = Task.Run(() => WriteAsync(level, source, message, ex));
        }

        private static async Task WriteAsync(Level level, string source, string message, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"extension-{DateTime.Now:yyyy-MM-dd}.log");

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
                sb.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ");
                sb.Append('[').Append(source ?? "-").Append("] ");
                sb.AppendLine(message ?? "");
                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                }

                await WriteLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
                finally
                {
                    WriteLock.Release();
                }
            }
            catch
            {
                // Disk full, AV lock, permissions. Logging must never throw.
            }
        }
    }
}
