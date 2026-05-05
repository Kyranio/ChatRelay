namespace ChatRelay.Changes;

/// <summary>
/// Tiny line-count helper. We only need <c>+N / -M</c> totals for the chat-side
/// changes list — full hunk computation lands in the future inline-editor phase.
///
/// Implements the "shortest edit script" length via the classic O(ND) Myers
/// algorithm restricted to line granularity. For the file sizes we deal with
/// (typical source files, &lt; 5k lines) this is microseconds and avoids
/// pulling in DiffPlex as a dependency.
/// </summary>
public static class LineDiff
{
    public readonly record struct Counts(int Added, int Removed);

    public static Counts Compute(string before, string after)
    {
        if (ReferenceEquals(before, after)) return new Counts(0, 0);
        if (before == after) return new Counts(0, 0);
        var a = SplitLines(before);
        var b = SplitLines(after);
        return ComputeMyers(a, b);
    }

    // Splits on \n and trims a single trailing \r per line. Empty trailing
    // newline produces an empty final element which we drop, matching Git's
    // line-counting behaviour ("a file ending with \n has N lines, not N+1").
    static string[] SplitLines(string s)
    {
        if (s.Length == 0) return [];
        var raw = s.Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            if (line.Length > 0 && line[^1] == '\r') raw[i] = line[..^1];
        }
        // Drop the trailing empty element produced by a final '\n'.
        if (raw.Length > 0 && raw[^1].Length == 0)
        {
            var trimmed = new string[raw.Length - 1];
            Array.Copy(raw, trimmed, trimmed.Length);
            return trimmed;
        }
        return raw;
    }

    // Myers diff, line-level. Returns the sum of insertions and deletions
    // separately so callers can render "+N / -M".
    //
    // We lean on the symmetric forward-search variant: the "edit distance"
    // D = added + removed, but we can't recover the split from D alone, so
    // we run the full backtrace — still O((N+M)·D) which is fine here.
    static Counts ComputeMyers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;

        // Trim common prefix/suffix to make the inner search cheaper on
        // typical edits (most of a source file is unchanged).
        int prefix = 0;
        while (prefix < n && prefix < m && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && a[n - 1 - suffix] == b[m - 1 - suffix]) suffix++;

        int aLen = n - prefix - suffix;
        int bLen = m - prefix - suffix;
        if (aLen == 0) return new Counts(bLen, 0);
        if (bLen == 0) return new Counts(0, aLen);

        // V[k] tracks the furthest-reaching x for diagonal k. Standard
        // Myers indexing with offset = max(N,M) so negative k fits.
        int max = aLen + bLen;
        var v = new int[2 * max + 1];
        int offset = max;

        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];        // step down: insertion
                else
                    x = v[offset + k - 1] + 1;    // step right: deletion
                int y = x - k;
                while (x < aLen && y < bLen && a[prefix + x] == b[prefix + y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= aLen && y >= bLen)
                {
                    // d = added + removed; we just need the split. Walk
                    // back along the same V to count each step's type.
                    return BacktraceCounts(a, b, prefix, aLen, bLen, d, max);
                }
            }
        }
        // Should never hit — the loop above always returns by d == max.
        return new Counts(bLen, aLen);
    }

    // Re-runs the forward search recording the chosen direction at every
    // step so we can attribute each unit of D to "added" vs "removed".
    static Counts BacktraceCounts(string[] a, string[] b, int prefix, int aLen, int bLen, int totalD, int max)
    {
        // Snapshot V at every D-iteration, then walk back from (aLen, bLen).
        var trace = new int[totalD + 1][];
        var v = new int[2 * max + 1];
        int offset = max;

        for (int d = 0; d <= totalD; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];
                else
                    x = v[offset + k - 1] + 1;
                int y = x - k;
                while (x < aLen && y < bLen && a[prefix + x] == b[prefix + y]) { x++; y++; }
                v[offset + k] = x;
            }
            trace[d] = (int[])v.Clone();
        }

        int added = 0, removed = 0;
        int xCur = aLen, yCur = bLen;
        for (int d = totalD; d > 0; d--)
        {
            int k = xCur - yCur;
            var prev = trace[d - 1];
            int prevK = (k == -d || (k != d && prev[offset + k - 1] < prev[offset + k + 1]))
                ? k + 1   // came from insertion
                : k - 1;  // came from deletion
            int prevX = prev[offset + prevK];
            int prevY = prevX - prevK;
            // Skip the matched diagonal run between (prevX, prevY) and
            // (xCur, yCur) — those were unchanged lines and don't count.
            if (prevK == k + 1) added++; else removed++;
            xCur = prevX;
            yCur = prevY;
        }
        return new Counts(added, removed);
    }
}
