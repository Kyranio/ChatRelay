using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatRelay.Backends;

/// <summary>
/// Turns Claude CLI model ids (e.g. <c>claude-opus-4-5-20250929</c>) into
/// human-friendly names (<c>Claude Opus 4.5</c>) for the assistant bubble label.
/// </summary>
public static class ModelNameFormatter
{
    public const string Fallback = "Claude";

    /// <summary>
    /// <c>claude-opus-4-5-…</c> → <c>Claude Opus 4.5</c>,
    /// <c>claude-sonnet-4-…</c> → <c>Claude Sonnet 4</c>,
    /// <c>claude-future-tier-…</c> → <c>Claude Future</c>.
    /// Tier is any lowercase word (not hardcoded opus/sonnet/haiku) so new
    /// families announced later still render sensibly. Minor version digit
    /// is optional, so both two-part (`4`) and three-part (`4-5`) ids
    /// produce a clean label. Returns <see cref="Fallback"/> on anything
    /// that doesn't start with <c>claude-&lt;word&gt;</c>.
    /// </summary>
    public static string FormatModelId(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return Fallback;
        var m = Regex.Match(
            modelId,
            @"^claude-([a-z]+)(?:-(\d+))?(?:-(\d+))?",
            RegexOptions.IgnoreCase);
        if (!m.Success) return Fallback;
        var raw = m.Groups[1].Value;
        var tier = char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();

        var major = m.Groups[2].Success ? m.Groups[2].Value : null;
        var minor = m.Groups[3].Success ? m.Groups[3].Value : null;

        if (string.IsNullOrEmpty(major)) return $"Claude {tier}";
        if (string.IsNullOrEmpty(minor)) return $"Claude {tier} {major}";
        return $"Claude {tier} {major}.{minor}";
    }

    /// <summary>
    /// Pulls the <c>model</c> field from a <c>system</c> stream-json event and
    /// formats it; falls back to <paramref name="fallback"/> on parse failure
    /// or missing field so a bad event doesn't clobber a previously-good name.
    /// </summary>
    public static string? TryExtractFromSystemEvent(string? json, string? fallback)
    {
        if (json == null) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String)
            {
                return FormatModelId(model.GetString());
            }
        }
        catch { /* malformed event — keep previous name */ }
        return fallback;
    }
}
