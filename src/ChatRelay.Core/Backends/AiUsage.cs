using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Token / cost accounting for one assistant turn. Adapters fill in what
    /// they know; everything else stays zero / null. Aggregated into session
    /// totals for the status bar by <see cref="Sum"/>.
    /// </summary>
    public class AiUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>Tokens re-used from prompt cache (Anthropic cache reads). 0 when n/a.</summary>
        public int CacheReadTokens { get; set; }

        /// <summary>Tokens written into prompt cache (Anthropic cache writes). 0 when n/a.</summary>
        public int CacheWriteTokens { get; set; }

        /// <summary>USD cost for this turn. Populated by adapters that hand it back directly (Claude CLI). Null elsewhere.</summary>
        public double? CostUsd { get; set; }

        public bool HasAnything =>
            InputTokens > 0 || OutputTokens > 0 ||
            CacheReadTokens > 0 || CacheWriteTokens > 0 || CostUsd.HasValue;

        /// <summary>Per-turn display, e.g. "3,420 in · 812 out · $0.0071".</summary>
        public string FormatPerTurn()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(InputTokens.ToString("N0", ci)).Append(" in · ")
              .Append(OutputTokens.ToString("N0", ci)).Append(" out");
            if (CostUsd.HasValue)
                sb.Append(" · $").Append(CostUsd.Value.ToString("F4", ci));
            return sb.ToString();
        }

        /// <summary>Session total display, e.g. "Session: 48,302 in · 12,108 out · $0.1234".</summary>
        public string FormatSessionTotal()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("Session: ");
            sb.Append(InputTokens.ToString("N0", ci)).Append(" in · ")
              .Append(OutputTokens.ToString("N0", ci)).Append(" out");
            if (CostUsd.HasValue)
                sb.Append(" · $").Append(CostUsd.Value.ToString("F4", ci));
            return sb.ToString();
        }

        public static AiUsage Sum(IEnumerable<AiUsage> usages)
        {
            var total = new AiUsage();
            if (usages == null) return total;
            foreach (var u in usages)
            {
                if (u == null) continue;
                total.InputTokens += u.InputTokens;
                total.OutputTokens += u.OutputTokens;
                total.CacheReadTokens += u.CacheReadTokens;
                total.CacheWriteTokens += u.CacheWriteTokens;
                if (u.CostUsd.HasValue) total.CostUsd = (total.CostUsd ?? 0) + u.CostUsd.Value;
            }
            return total;
        }
    }
}
