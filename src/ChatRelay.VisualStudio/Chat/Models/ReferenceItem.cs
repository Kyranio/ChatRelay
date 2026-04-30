using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace ChatRelay.Chat.Models
{
    /// <summary>
    /// One pinned reference, keyed by file. Either a whole-file reference
    /// (<see cref="Ranges"/> empty, <see cref="FullContent"/> populated) or
    /// one-or-more line-range selections within that file.
    /// Implements <see cref="INotifyPropertyChanged"/> so chips update live
    /// when ranges get merged in place.
    /// </summary>
    public sealed class ReferenceItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; } = string.Empty;      // "@relative/path.cs"
        public string AbsolutePath { get; set; } = string.Empty;
        public List<LineRange> Ranges { get; } = new List<LineRange>();
        public string? FullContent { get; set; }

        public bool IsWholeFile => Ranges.Count == 0 && FullContent != null;

        /// <summary>
        /// Display suffix for the chip:
        ///   0 ranges        → ""
        ///   1 range         → " :1-3"
        ///   2 ranges        → " :1-3 & 11-25"
        ///   3+ ranges       → " :1-3, 11-25 & 100-204"
        /// </summary>
        public string LineRangesDisplay
        {
            get
            {
                var count = Ranges.Count;
                if (count == 0) return string.Empty;
                if (count == 1) return " " + Ranges[0].Display;

                var parts = new string[count];
                for (int i = 0; i < count; i++) parts[i] = Ranges[i].Display.TrimStart(':');

                var sb = new StringBuilder(" :");
                for (int i = 0; i < count - 1; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parts[i]);
                }
                sb.Append(" & ").Append(parts[count - 1]);
                return sb.ToString();
            }
        }

        // For chip navigation. Multi-range refs point at the first range;
        // individual range clicks go to their specific range directly.
        public int StartLine => Ranges.Count > 0 ? Ranges[0].Start : 0;
        public int EndLine => Ranges.Count > 0 ? Ranges[0].End : 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Fire PropertyChanged for every derived property, to refresh bound chips after Ranges / FullContent are mutated in place.</summary>
        public void NotifyChanged()
        {
            var h = PropertyChanged;
            if (h == null) return;
            h(this, new PropertyChangedEventArgs(nameof(IsWholeFile)));
            h(this, new PropertyChangedEventArgs(nameof(LineRangesDisplay)));
            h(this, new PropertyChangedEventArgs(nameof(StartLine)));
            h(this, new PropertyChangedEventArgs(nameof(EndLine)));
        }

        /// <summary>
        /// Sort <see cref="Ranges"/> by <see cref="LineRange.Start"/> and coalesce
        /// any pair whose line regions overlap (share at least one line). Strictly
        /// adjacent ranges like 10-15 and 16-20 stay separate. When a merge
        /// happens the surviving body is stitched together from the two
        /// ranges' already-captured bodies — we do NOT re-read the file from
        /// disk, so each range stays the frozen snapshot the user saw at pin
        /// time (consistent with how sent history is treated elsewhere).
        /// </summary>
        public void MergeOverlappingRanges()
        {
            Ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

            for (int i = 0; i < Ranges.Count - 1;)
            {
                var a = Ranges[i];
                var b = Ranges[i + 1];
                if (a.End >= b.Start)
                {
                    // Take the tail of b that extends beyond a (if any) and
                    // append it to a's existing body. bLines is indexed
                    // relative to b.Start; the first line beyond a.End sits
                    // at (a.End + 1 - b.Start).
                    if (b.End > a.End && !string.IsNullOrEmpty(b.Body))
                    {
                        var bLines = b.Body!.Replace("\r\n", "\n").Split('\n');
                        var tailIndex = System.Math.Max(0, a.End + 1 - b.Start);
                        if (tailIndex < bLines.Length)
                        {
                            var tail = string.Join("\n", bLines.Skip(tailIndex));
                            a.Body = string.IsNullOrEmpty(a.Body) ? tail : a.Body + "\n" + tail;
                        }
                    }
                    a.End = System.Math.Max(a.End, b.End);
                    Ranges.RemoveAt(i + 1);
                }
                else i++;
            }
        }

        /// <summary>
        /// Append this reference to a CLI prompt:
        /// one <c>Reference: @file.cs</c> line + fence for whole-file refs,
        /// or one <c>Reference: @file.cs:A-B</c> + fence per range for selection refs.
        /// Empty bodies are silently skipped (no empty fences).
        /// </summary>
        public void AppendToPrompt(StringBuilder prompt)
        {
            if (IsWholeFile)
            {
                prompt.AppendLine($"Reference: {FilePath}");
                if (!string.IsNullOrEmpty(FullContent))
                {
                    prompt.AppendLine("```");
                    prompt.AppendLine(FullContent);
                    prompt.AppendLine("```");
                }
                prompt.AppendLine();
                return;
            }

            foreach (var range in Ranges)
            {
                prompt.AppendLine($"Reference: {FilePath}{range.Display}");
                if (!string.IsNullOrEmpty(range.Body))
                {
                    prompt.AppendLine("```");
                    prompt.AppendLine(range.Body);
                    prompt.AppendLine("```");
                }
                prompt.AppendLine();
            }
        }
    }

    public sealed class LineRange
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string? Body { get; set; }

        public string Display => Start == End ? $":{Start}" : $":{Start}-{End}";
    }
}
