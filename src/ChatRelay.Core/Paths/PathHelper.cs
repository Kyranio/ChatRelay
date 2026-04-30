using System;
using System.IO;

namespace ChatRelay.Paths
{
    /// <summary>
    /// Path-comparison helpers used in multiple places (MCP file registry,
    /// permission-bubble sandbox check, project-boundary validation).
    /// Central so the normalisation rules (case-insensitive, trailing-slash
    /// agnostic, full-path expansion) stay consistent.
    /// </summary>
    public static class PathHelper
    {
        /// <summary>
        /// Case-insensitive equality comparison with trailing separator
        /// and full-path normalisation. <c>C:\x</c> and <c>C:/x/</c>
        /// compare equal. Nulls and empties compare equal to each other.
        /// Any normalisation failure falls back to raw string comparison.
        /// </summary>
        public static bool Equals(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Normalise(a!), Normalise(b!),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// True when <paramref name="path"/> is equal to or nested inside
        /// <paramref name="root"/>. Both sides are normalised before the
        /// startsWith check, so forward/back slashes and trailing
        /// separators don't trip the comparison.
        /// </summary>
        public static bool IsUnder(string? path, string? root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                var p = Normalise(path!);
                var r = Normalise(root!);
                return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Normalise(string path)
            => Path.GetFullPath(path).TrimEnd('\\', '/');
    }
}
