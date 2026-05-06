using System;
using System.Collections.Generic;
using System.Diagnostics;
using ChatRelay.Host;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace ChatRelay.Editor;

/// <summary>
/// Per-editor-view glue between <see cref="EditorChangesService"/> and the
/// adornment layer. One instance lives for the lifetime of one
/// <see cref="IWpfTextView"/>:
/// <list type="bullet">
///   <item>Resolves the view's underlying file path (via the buffer's
///   <see cref="ITextDocument"/> property).</item>
///   <item>Subscribes to <see cref="EditorChangesService.HunksChanged"/>
///   filtered to its file.</item>
///   <item>Currently logs hunk arrivals — Phase 4.3 will replace the log
///   with actual rendering on the <c>ChatRelayHunks</c> adornment layer.</item>
///   <item>Unsubscribes on view close to keep the service's invocation
///   list bounded.</item>
/// </list>
/// </summary>
sealed class HunkAdornmentManager
{
    readonly IWpfTextView _view;
    readonly string? _filePath;
    readonly EditorChangesService _service;

    public HunkAdornmentManager(IWpfTextView view)
    {
        _view = view;
        _filePath = ResolveFilePath(view);
        _service = EditorChangesService.Current;

        if (_filePath is null)
        {
            // No path means this is some virtual / non-document buffer —
            // nothing to track. The manager still exists tied to the view
            // lifetime so VS can clean it up; we simply don't subscribe.
            return;
        }

        _service.HunksChanged += OnHunksChanged;
        _view.Closed += OnViewClosed;

        // Pick up any hunks already known for this file (the snapshot may
        // have arrived before the editor view opened).
        var initial = _service.GetHunks(_filePath);
        if (initial.Count > 0) RenderHunks(initial);
    }

    void OnHunksChanged(string changedPath)
    {
        if (_filePath is null) return;
        if (!string.Equals(changedPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        var hunks = _service.GetHunks(_filePath);
        RenderHunks(hunks);
    }

    void RenderHunks(IReadOnlyList<HunkInfo> hunks)
    {
        // Phase 4.2 placeholder. Phase 4.3 replaces this with actual
        // adornment rendering on the ChatRelayHunks layer:
        //   - block adornment per hunk anchored to NewStart..NewStart+NewCount
        //   - WPF panel with red-tinted old lines + green-tinted new lines
        //   - floating accept (✓) / reject (×) buttons on the side
        // For now we just log so the wiring can be verified end-to-end
        // (and so a Phase 4.3 implementer has a known confirmation point).
        // System.Diagnostics.Debug → VS's output window during F5 of the
        // experimental instance. The host-side logger lives in net10
        // territory (ChatRelay.Logging) and isn't reachable from this
        // net48 assembly.
        Debug.WriteLine($"[ChatRelay.editor] Hunks for {_filePath}: {hunks.Count} hunk(s) " +
            $"({CountByState(hunks, "open")} open, {CountByState(hunks, "accepted")} accepted)");
    }

    static int CountByState(IReadOnlyList<HunkInfo> hunks, string state)
    {
        int count = 0;
        for (int i = 0; i < hunks.Count; i++)
            if (string.Equals(hunks[i].State, state, StringComparison.Ordinal)) count++;
        return count;
    }

    void OnViewClosed(object? sender, EventArgs e)
    {
        _service.HunksChanged -= OnHunksChanged;
        _view.Closed -= OnViewClosed;
    }

    static string? ResolveFilePath(IWpfTextView view)
    {
        // The path lives on the ITextDocument that was attached to the
        // buffer when the file was opened. Some views (e.g. interactive
        // shells, peek frames) won't have one — return null and the
        // manager skips subscription.
        if (view.TextBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var doc))
            return doc.FilePath;
        return null;
    }
}
