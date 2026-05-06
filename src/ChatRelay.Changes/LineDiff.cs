namespace ChatRelay.Changes;

/// <summary>
/// Line-level diff helper. Powers two outputs from the same Myers-O(ND) walk:
/// <list type="bullet">
///   <item><see cref="Compute"/> returns aggregate <c>+N / -M</c> counts for
///   the chat-side changes list.</item>
///   <item><see cref="ComputeHunks"/> returns a structured list of edit hunks
///   for per-hunk accept / reject and inline-editor rendering.</item>
/// </list>
///
/// <para>
/// Implementation notes — Myers' "shortest edit script" forward-search with a
/// per-step trace, then a single backtrace. For typical source files
/// (&lt; 5k lines) this is microseconds and avoids pulling in DiffPlex as a
/// dependency.
/// </para>
/// </summary>
public static class LineDiff
{
    public readonly record struct Counts(int Added, int Removed);

    /// <summary>
    /// One contiguous edit between matched regions. Coordinates are 0-based
    /// line indices into the <i>original</i> string content (lines split on
    /// <c>\n</c> with trailing <c>\r</c> trimmed). A pure insertion has
    /// <see cref="OldCount"/> = 0 and conceptually means "insert at line
    /// <see cref="OldStart"/>"; a pure deletion has <see cref="NewCount"/> = 0.
    /// </summary>
    public readonly record struct Hunk(
        int OldStart,
        int OldCount,
        int NewStart,
        int NewCount,
        IReadOnlyList<string> OldLines,
        IReadOnlyList<string> NewLines);

    public static Counts Compute(string before, string after)
    {
        if (ReferenceEquals(before, after)) return new Counts(0, 0);
        if (before == after) return new Counts(0, 0);
        var a = SplitLines(before);
        var b = SplitLines(after);
        var script = ComputeEditScript(a, b);
        int added = 0, removed = 0;
        foreach (var op in script)
        {
            if (op.Kind == EditKind.Insert) added++;
            else if (op.Kind == EditKind.Delete) removed++;
        }
        return new Counts(added, removed);
    }

    public static IReadOnlyList<Hunk> ComputeHunks(string before, string after)
    {
        if (ReferenceEquals(before, after)) return Array.Empty<Hunk>();
        if (before == after) return Array.Empty<Hunk>();
        var a = SplitLines(before);
        var b = SplitLines(after);
        var script = ComputeEditScript(a, b);
        return GroupHunks(script, a, b);
    }

    // ---- Internals ---------------------------------------------------

    enum EditKind { Match, Delete, Insert }

    readonly record struct EditOp(EditKind Kind, int OldIndex, int NewIndex);

    /// <summary>
    /// Walks Myers forward, then backtraces to produce a forward-ordered edit
    /// script covering both inputs in full (every old line and every new line
    /// is referenced by exactly one op). Common prefixes / suffixes are
    /// emitted as <see cref="EditKind.Match"/> ops without entering the
    /// inner Myers loop.
    /// </summary>
    static List<EditOp> ComputeEditScript(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var ops = new List<EditOp>(Math.Max(n, m));

        // Common prefix — straight matches.
        int prefix = 0;
        while (prefix < n && prefix < m && a[prefix] == b[prefix])
        {
            ops.Add(new EditOp(EditKind.Match, prefix, prefix));
            prefix++;
        }

        // Common suffix — record indices, emit at the end after the inner ops.
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && a[n - 1 - suffix] == b[m - 1 - suffix]) suffix++;

        int aLen = n - prefix - suffix;
        int bLen = m - prefix - suffix;

        if (aLen == 0 && bLen == 0)
        {
            // Pure prefix + suffix — nothing inner to do.
        }
        else if (aLen == 0)
        {
            for (int j = 0; j < bLen; j++)
                ops.Add(new EditOp(EditKind.Insert, prefix /* phantom old */, prefix + j));
        }
        else if (bLen == 0)
        {
            for (int i = 0; i < aLen; i++)
                ops.Add(new EditOp(EditKind.Delete, prefix + i, prefix /* phantom new */));
        }
        else
        {
            AppendInnerOps(a, b, prefix, aLen, bLen, ops);
        }

        // Common suffix matches.
        for (int s = 0; s < suffix; s++)
            ops.Add(new EditOp(EditKind.Match, n - suffix + s, m - suffix + s));

        return ops;
    }

    // Runs Myers' forward search on a[prefix..prefix+aLen) vs b[prefix..prefix+bLen),
    // captures V at every D iteration, then backtraces appending forward-ordered
    // EditOps onto <paramref name="ops"/>.
    static void AppendInnerOps(string[] a, string[] b, int prefix, int aLen, int bLen, List<EditOp> ops)
    {
        int max = aLen + bLen;
        var v = new int[2 * max + 1];
        int offset = max;
        var trace = new List<int[]>();

        int totalD = 0;
        bool found = false;
        for (int d = 0; d <= max && !found; d++)
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
                if (x >= aLen && y >= bLen)
                {
                    totalD = d;
                    trace.Add((int[])v.Clone());
                    found = true;
                    break;
                }
            }
            if (!found) trace.Add((int[])v.Clone());
        }

        // Backtrace, appending ops in reverse order.
        var reverse = new List<EditOp>();
        int xCur = aLen, yCur = bLen;
        for (int d = totalD; d > 0; d--)
        {
            int k = xCur - yCur;
            var prev = trace[d - 1];
            bool isInsertion = k == -d || (k != d && prev[offset + k - 1] < prev[offset + k + 1]);
            int prevK = isInsertion ? k + 1 : k - 1;
            int prevX = prev[offset + prevK];
            int prevY = prevX - prevK;

            // Matches between (afterStep) and (xCur, yCur) — diagonal run.
            int afterStepX = isInsertion ? prevX : prevX + 1;
            int afterStepY = isInsertion ? prevY + 1 : prevY;
            for (int x = xCur - 1, y = yCur - 1; x >= afterStepX && y >= afterStepY; x--, y--)
                reverse.Add(new EditOp(EditKind.Match, prefix + x, prefix + y));

            // The single I/D step.
            if (isInsertion)
                reverse.Add(new EditOp(EditKind.Insert, prefix + prevX, prefix + prevY));
            else
                reverse.Add(new EditOp(EditKind.Delete, prefix + prevX, prefix + prevY));

            xCur = prevX;
            yCur = prevY;
        }
        // d=0 leading matches, from (0, 0) to (xCur, yCur) along the diagonal.
        for (int x = xCur - 1; x >= 0; x--)
            reverse.Add(new EditOp(EditKind.Match, prefix + x, prefix + x));

        // Reverse-into-place: we collected back-to-front.
        for (int i = reverse.Count - 1; i >= 0; i--) ops.Add(reverse[i]);
    }

    static List<Hunk> GroupHunks(List<EditOp> ops, string[] a, string[] b)
    {
        var hunks = new List<Hunk>();
        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == EditKind.Match) { i++; continue; }

            int hunkStartOld = ops[i].OldIndex;
            int hunkStartNew = ops[i].NewIndex;
            var oldLs = new List<string>();
            var newLs = new List<string>();
            while (i < ops.Count && ops[i].Kind != EditKind.Match)
            {
                if (ops[i].Kind == EditKind.Delete) oldLs.Add(a[ops[i].OldIndex]);
                else newLs.Add(b[ops[i].NewIndex]);
                i++;
            }
            hunks.Add(new Hunk(hunkStartOld, oldLs.Count, hunkStartNew, newLs.Count, oldLs, newLs));
        }
        return hunks;
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

    /// <summary>
    /// Splices <paramref name="newLines"/> into <paramref name="content"/> at
    /// line position <paramref name="oldStart"/>, removing <paramref name="oldCount"/>
    /// lines first. Preserves the newline style of the original (CRLF if any
    /// CRLF is present, LF otherwise) and the original's trailing-newline
    /// presence. Used by the per-hunk accept / reject paths to mutate
    /// Baseline / Accepted / LastApplied blobs.
    /// </summary>
    public static string SpliceLines(string content, int oldStart, int oldCount, IReadOnlyList<string> newLines)
    {
        // Detect newline style. Default to LF for empty content.
        string newline = content.Contains("\r\n") ? "\r\n" : "\n";

        int startOffset = FindLineOffset(content, oldStart);
        int endOffset = FindLineOffset(content, oldStart + oldCount);

        var sb = new System.Text.StringBuilder(content.Length + EstimateInsertSize(newLines, newline));
        sb.Append(content, 0, startOffset);

        // If we're inserting and the preceding content doesn't end with a
        // newline (e.g. inserting at end-of-file in a file without a trailing
        // newline), we need to add one before the new content.
        bool needsLeadingNewline = newLines.Count > 0
            && startOffset > 0
            && content[startOffset - 1] != '\n';
        if (needsLeadingNewline) sb.Append(newline);

        for (int i = 0; i < newLines.Count; i++)
        {
            sb.Append(newLines[i]);
            // Add a newline after each new line UNLESS this is the last new
            // line AND there's no content after the splice point. Matches
            // the original file's "trailing newline yes/no" choice.
            bool isLast = i == newLines.Count - 1;
            if (!isLast || endOffset < content.Length)
                sb.Append(newline);
        }

        sb.Append(content, endOffset, content.Length - endOffset);
        return sb.ToString();
    }

    static int EstimateInsertSize(IReadOnlyList<string> lines, string newline)
    {
        int size = 0;
        for (int i = 0; i < lines.Count; i++) size += lines[i].Length + newline.Length;
        return size;
    }

    // Returns the character offset where line <paramref name="lineIndex"/>
    // begins in <paramref name="content"/>. lineIndex == content's line count
    // returns content.Length (one past the end). Lines are counted by '\n'
    // boundaries.
    static int FindLineOffset(string content, int lineIndex)
    {
        if (lineIndex == 0) return 0;
        int line = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                if (line == lineIndex) return i + 1;
            }
        }
        // No-trailing-newline edge case: line N starts at end-of-content
        // when content has N-1 newlines (i.e. line N is the next "virtual"
        // line past the last actual line).
        if (line == lineIndex - 1) return content.Length;
        return content.Length;     // out of range — clamp to end
    }
}
