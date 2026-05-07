using DiffPlex;

namespace ChatRelay.Changes;

/// <summary>
/// Line-level diff helper backed by DiffPlex. Two outputs:
/// <list type="bullet">
///   <item><see cref="Compute"/> — aggregate <c>+N / -M</c> counts.</item>
///   <item><see cref="ComputeHunks"/> — coalesced edit hunks for per-hunk
///   accept / reject and inline-editor rendering.</item>
/// </list>
///
/// <para>
/// Hunks split <i>only</i> when real preserved code sits between two
/// changed regions. Adjacent change blocks separated solely by
/// "noise" matched lines — blanks plus structural punctuation
/// (<c>{ } ( ) ; ,</c>) the diff happened to align — are merged into
/// one hunk. Effect: filling an empty class is one big hunk; adding
/// code above and below an existing method produces two hunks
/// (the method body is real preserved code); replacing a method is
/// one hunk with the old body in the red strip (the signature/body
/// replacements coalesce across the matched braces between them).
/// </para>
/// </summary>
public static class LineDiff
{

    public readonly record struct Counts(int Added, int Removed);

    /// <summary>
    /// One contiguous edit between matched regions. Coordinates are 0-based
    /// line indices into the <i>original</i> string content (lines split on
    /// <c>\n</c> with trailing <c>\r</c> trimmed). After coalescing,
    /// <see cref="OldLines"/> / <see cref="NewLines"/> may include unchanged
    /// "context" lines from the small gaps that were merged across.
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
        if (ReferenceEquals(before, after) || before == after) return new Counts(0, 0);
        var result = Differ.Instance.CreateCustomDiffs(before, after, ignoreWhiteSpace: false, SplitLines);
        int added = 0, removed = 0;
        foreach (var b in result.DiffBlocks)
        {
            added += b.InsertCountB;
            removed += b.DeleteCountA;
        }
        return new Counts(added, removed);
    }

    public static IReadOnlyList<Hunk> ComputeHunks(string before, string after)
    {
        if (ReferenceEquals(before, after) || before == after) return Array.Empty<Hunk>();
        var result = Differ.Instance.CreateCustomDiffs(before, after, ignoreWhiteSpace: false, SplitLines);
        var blocks = result.DiffBlocks;
        if (blocks.Count == 0) return Array.Empty<Hunk>();

        // Walk blocks in order, merging into the running (oldStart, oldEnd,
        // newStart, newEnd) when the matched lines between two change
        // blocks are all "noise" (whitespace + structural punctuation).
        // Those don't represent real preserved code worth splitting a
        // hunk around — they're just braces and blank lines the diff
        // aligned by accident. Any matched line with actual identifiers
        // / keywords / literals ends the current hunk. Old/new gap
        // content is identical (matched runs come from both sides), so
        // we inspect PiecesOld.
        var hunks = new List<Hunk>();
        int oStart = blocks[0].DeleteStartA;
        int oEnd = oStart + blocks[0].DeleteCountA;
        int nStart = blocks[0].InsertStartB;
        int nEnd = nStart + blocks[0].InsertCountB;
        for (int i = 1; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (AllNoise(result.PiecesOld, oEnd, b.DeleteStartA))
            {
                oEnd = b.DeleteStartA + b.DeleteCountA;
                nEnd = b.InsertStartB + b.InsertCountB;
            }
            else
            {
                hunks.Add(BuildHunk(result.PiecesOld, result.PiecesNew, oStart, oEnd, nStart, nEnd));
                oStart = b.DeleteStartA;
                oEnd = oStart + b.DeleteCountA;
                nStart = b.InsertStartB;
                nEnd = nStart + b.InsertCountB;
            }
        }
        hunks.Add(BuildHunk(result.PiecesOld, result.PiecesNew, oStart, oEnd, nStart, nEnd));
        return hunks;
    }

    // A line is "noise" if it's blank or contains only structural
    // punctuation. Anything with letters, digits, or operator symbols is
    // real code and should split a hunk when matched between two change
    // blocks.
    static bool AllNoise(IReadOnlyList<string> lines, int start, int endExclusive)
    {
        for (int i = start; i < endExclusive; i++)
            if (!IsNoiseLine(lines[i])) return false;
        return true;
    }

    static bool IsNoiseLine(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '{' || c == '}' || c == '(' || c == ')' || c == ';' || c == ',') continue;
            return false;
        }
        return true;
    }

    static Hunk BuildHunk(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int oStart, int oEnd, int nStart, int nEnd) =>
        new(oStart, oEnd - oStart, nStart, nEnd - nStart, Slice(oldLines, oStart, oEnd - oStart), Slice(newLines, nStart, nEnd - nStart));

    static string[] Slice(IReadOnlyList<string> arr, int start, int count)
    {
        if (count == 0) return Array.Empty<string>();
        var s = new string[count];
        for (int i = 0; i < count; i++) s[i] = arr[start + i];
        return s;
    }

    // Splits on \n and trims a single trailing \r per line. Empty trailing
    // newline produces an empty final element which we drop, matching Git's
    // line-counting behaviour ("a file ending with \n has N lines, not N+1").
    // Used as the chunker for DiffPlex so block indices line up with the
    // line indices SpliceLines expects.
    static string[] SplitLines(string s)
    {
        if (s.Length == 0) return [];
        var raw = s.Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            if (line.Length > 0 && line[^1] == '\r') raw[i] = line[..^1];
        }
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
        string newline = content.Contains("\r\n") ? "\r\n" : "\n";

        int startOffset = FindLineOffset(content, oldStart);
        int endOffset = FindLineOffset(content, oldStart + oldCount);

        var sb = new System.Text.StringBuilder(content.Length + EstimateInsertSize(newLines, newline));
        sb.Append(content, 0, startOffset);

        // Inserting at end-of-file in a file without a trailing newline:
        // add a separator newline before the new content.
        bool needsLeadingNewline = newLines.Count > 0
            && startOffset > 0
            && content[startOffset - 1] != '\n';
        if (needsLeadingNewline) sb.Append(newline);

        // Trailing-newline preservation: if the splice consumes the final
        // line(s) of the file AND the original ended with a newline, the
        // result must end with one too — otherwise downstream byte-equality
        // checks (FileTracker.HasProposal) flag a non-existent diff while
        // ComputeHunks correctly sees identical line arrays.
        bool originalEndsWithNewline = content.Length > 0
            && content[content.Length - 1] == '\n';

        for (int i = 0; i < newLines.Count; i++)
        {
            sb.Append(newLines[i]);
            bool isLast = i == newLines.Count - 1;
            bool needTrailing = !isLast
                || endOffset < content.Length
                || originalEndsWithNewline;
            if (needTrailing) sb.Append(newline);
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
    // returns content.Length (one past the end).
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
        if (line == lineIndex - 1) return content.Length;
        return content.Length;
    }
}
