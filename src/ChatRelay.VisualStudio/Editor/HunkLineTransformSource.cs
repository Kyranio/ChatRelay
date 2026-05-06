using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace ChatRelay.Editor;

/// <summary>
/// Reserves vertical space ABOVE selected buffer lines so the hunk
/// renderer can paint a "removed lines" inline block in the gap. This
/// is the same primitive CodeLens / inline-references use to inject
/// non-content visual rows between source lines without affecting line
/// numbering or file content.
///
/// <para>
/// Per-view singleton. The <see cref="HunkAdornmentManager"/> looks the
/// instance up via <see cref="IPropertyOwner.Properties"/>, asks it to
/// reserve top-space on the lines that anchor an open hunk's removed
/// content, and then paints the red block adornment positioned in that
/// reserved gap. When the hunk set or buffer changes, the manager
/// pushes a new map and the source forces a layout refresh so VS
/// recomputes line transforms.
/// </para>
/// </summary>
public sealed class HunkLineTransformSource : ILineTransformSource
{
    readonly IWpfTextView _view;

    // line-number → reserved pixels above that line. Held by reference so
    // the manager's SetTopSpaces can swap atomically without tearing.
    Dictionary<int, double> _topSpaceByLine = new();

    public HunkLineTransformSource(IWpfTextView view) => _view = view;

    public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
    {
        // line is a formatted text line in the view; line.Start is the
        // buffer position of its first character. Resolve its line number
        // in the snapshot the line belongs to (not necessarily the view's
        // current snapshot — VS may be mid-format).
        int lineNum;
        try { lineNum = line.Snapshot.GetLineNumberFromPosition(line.Start); }
        catch { return new LineTransform(0, 0, 1.0); }

        return _topSpaceByLine.TryGetValue(lineNum, out var top)
            ? new LineTransform(top, 0, 1.0)
            : new LineTransform(0, 0, 1.0);
    }

    /// <summary>
    /// Replaces the per-line top-space reservations atomically and
    /// triggers a view refresh so the new transforms take effect.
    /// Returns true iff the reservations actually changed (the manager
    /// uses this to avoid spurious refresh loops).
    /// </summary>
    public bool SetTopSpaces(IDictionary<int, double> map)
    {
        if (DictionariesEqual(_topSpaceByLine, map)) return false;
        var fresh = new Dictionary<int, double>(map.Count);
        foreach (var kv in map) fresh[kv.Key] = kv.Value;
        _topSpaceByLine = fresh;
        // VS recomputes LineTransform during the next format pass; force
        // one by reformatting at the current top of the view. Without
        // this, the new top-spaces only apply to lines that happen to be
        // re-formatted for some other reason.
        try
        {
            if (_view.TextViewLines is { } lines && lines.FirstVisibleLine is { } first)
            {
                _view.DisplayTextLineContainingBufferPosition(
                    first.Start,
                    first.Top - _view.ViewportTop,
                    ViewRelativePosition.Top);
            }
        }
        catch (Exception)
        {
            // View might be closing or formatting; the next layout pass
            // will pick up the new map regardless.
        }
        return true;
    }

    static bool DictionariesEqual(Dictionary<int, double> a, IDictionary<int, double> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v)) return false;
            if (Math.Abs(v - kv.Value) > 0.5) return false;
        }
        return true;
    }
}

/// <summary>
/// MEF provider that VS calls once per view to obtain the per-view
/// <see cref="ILineTransformSource"/>. We stash the instance in the
/// view's property bag so <see cref="HunkAdornmentManager"/> can reach
/// it without going back through MEF.
/// </summary>
[Export(typeof(ILineTransformSourceProvider))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
public sealed class HunkLineTransformSourceProvider : ILineTransformSourceProvider
{
    public ILineTransformSource Create(IWpfTextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(
            typeof(HunkLineTransformSource),
            () => new HunkLineTransformSource(textView));
}
