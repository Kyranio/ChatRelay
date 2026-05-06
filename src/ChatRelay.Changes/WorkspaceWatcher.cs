using ChatRelay.Logging;

namespace ChatRelay.Changes;

/// <summary>
/// Recursive <see cref="FileSystemWatcher"/> wrapper for the workspace root.
/// Fires a callback whenever a file inside the directory changes; the
/// <see cref="ChangeTracker"/> uses this to invalidate denial entries that
/// are no longer redoable because the underlying file has drifted.
///
/// <para>
/// Idempotent: redundant events for the same file just trigger a re-read
/// + re-check on the tracker side. No debounce — the staleness check is
/// cheap (one disk read + a string compare) and missing an event is worse
/// than running it twice.
/// </para>
///
/// <para>
/// We do NOT try to filter out our own writes here. That decision belongs
/// to the tracker, which has the context to compare disk content against
/// known clean states (Baseline / LastApplied) and skip when they match.
/// </para>
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    readonly FileSystemWatcher _fsw;
    readonly Action<string> _onChange;

    public WorkspaceWatcher(string root, Action<string> onChange)
    {
        _onChange = onChange;
        _fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            // LastWrite covers content saves; FileName covers create/rename.
            // Size catches direct in-place writes that don't update LastWrite
            // mtime (NTFS sometimes coalesces). Attributes is omitted to
            // avoid spurious events from the VS file indexer.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            // Default 8 KB overflows on busy directories (build outputs,
            // node_modules-style trees). 64 KB is the conventional bump.
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = false,
        };
        _fsw.Changed += OnChanged;
        _fsw.Created += OnChanged;
        _fsw.Renamed += OnRenamed;
        _fsw.Error += OnError;
        _fsw.EnableRaisingEvents = true;
        ExtensionLogger.Info("changes", $"Watcher started on {root}");
    }

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Info-level on purpose — diagnostic for the "did the watcher fire?"
        // question. Once Phase 3 is stable, drop these to Debug so a busy
        // workspace doesn't spam the log.
        ExtensionLogger.Info("changes", $"Watcher {e.ChangeType}: {e.FullPath}");
        try { _onChange(e.FullPath); }
        catch (Exception ex)
        {
            ExtensionLogger.Warn("changes", "Watcher callback threw: " + ex.Message);
        }
    }

    void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Treat the rename target as a fresh write at its new path. The
        // old path is not touched here — orphaned tracker entries (if
        // any) stay until the session ends.
        ExtensionLogger.Info("changes", $"Watcher Renamed: {e.OldFullPath} -> {e.FullPath}");
        try { _onChange(e.FullPath); }
        catch (Exception ex)
        {
            ExtensionLogger.Warn("changes", "Watcher rename callback threw: " + ex.Message);
        }
    }

    void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow is the typical cause; log so we know but don't
        // try to recover. A missed event is acceptable — the next deliberate
        // user action (accept / deny / redo) will re-read disk and resolve.
        ExtensionLogger.Warn("changes",
            "FileSystemWatcher error: " + (e.GetException()?.Message ?? "<unknown>"));
    }

    public void Dispose()
    {
        try
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Changed -= OnChanged;
            _fsw.Created -= OnChanged;
            _fsw.Renamed -= OnRenamed;
            _fsw.Error -= OnError;
            _fsw.Dispose();
        }
        catch { }
    }
}
