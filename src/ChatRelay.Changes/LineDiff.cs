using DiffPlex;

namespace ChatRelay.Changes;

/// <summary>Line-level diff backed by DiffPlex with a noise-coalesce pass so one model edit shows as one logical hunk.</summary>
public static class LineDiff
{
    public readonly record struct Counts(int Added, int Removed);

    /// <summary>One contiguous edit. Coordinates are 0-based line indices into the original content.</summary>
    /// <remarks>After coalescing, OldLines/NewLines may include unchanged "noise" lines from gaps that were merged across.</remarks>
    public readonly record struct Hunk(
        int OldStart, int OldCount, int NewStart, int NewCount,
        IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines);

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

        // Coalesce when matched lines between two change blocks are all noise (whitespace + structural punctuation).
        // Old-side and new-side gap content is identical between change blocks, so inspecting PiecesOld suffices.
        var hunks = new List<Hunk>();
        int oStart = blocks[0].DeleteStartA, oEnd = oStart + blocks[0].DeleteCountA;
        int nStart = blocks[0].InsertStartB, nEnd = nStart + blocks[0].InsertCountB;
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

    // Split on \n, trim trailing \r, drop trailing-newline's empty final element (Git's line-counting).
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

    /// <summary>Splices newLines into content at line oldStart, removing oldCount lines. Preserves CRLF/LF style and trailing-newline presence.</summary>
    public static string SpliceLines(string content, int oldStart, int oldCount, IReadOnlyList<string> newLines)
    {
        string newline = content.Contains("\r\n") ? "\r\n" : "\n";
        int startOffset = FindLineOffset(content, oldStart);
        int endOffset = FindLineOffset(content, oldStart + oldCount);

        var sb = new System.Text.StringBuilder(content.Length + EstimateInsertSize(newLines, newline));
        sb.Append(content, 0, startOffset);

        // Inserting at EOF on a file without trailing newline: add the separator before the new content.
        if (newLines.Count > 0 && startOffset > 0 && content[startOffset - 1] != '\n') sb.Append(newline);

        // Preserve the original's trailing newline so byte-equality checks (HasProposal) match ComputeHunks's view.
        bool originalEndsWithNewline = content.Length > 0 && content[content.Length - 1] == '\n';

        for (int i = 0; i < newLines.Count; i++)
        {
            sb.Append(newLines[i]);
            bool isLast = i == newLines.Count - 1;
            if (!isLast || endOffset < content.Length || originalEndsWithNewline) sb.Append(newline);
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

    // Character offset where line lineIndex begins. lineIndex == line count returns content.Length.
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
        return content.Length;
    }
}
