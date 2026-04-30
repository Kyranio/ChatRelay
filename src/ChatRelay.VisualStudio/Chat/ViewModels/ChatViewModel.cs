using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChatRelay.Chat.Models;
using ChatRelay.Host;
using ChatRelay.Settings;

namespace ChatRelay.Chat.ViewModels;

/// <summary>
/// State and logic for the chat tool window. Owns the host client, the
/// session / model / reference collections, the current-session pointer,
/// and the streaming-text accumulators. Knows nothing about WPF — it
/// raises typed events that the view subscribes to and handles by
/// rendering bubbles.
/// <para>
/// Threading: this type is not thread-safe; the view is expected to call
/// every public member on the UI thread. The view subscribes to
/// <see cref="Host"/>'s background-thread events and wraps each
/// invocation in an <c>OnUi()</c> hop before calling into the VM. VM-
/// raised events therefore always fire on the UI thread.
/// </para>
/// </summary>
public sealed class ChatViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Public observable state --------------------------------------

    public ObservableCollection<ChatSession> Sessions { get; } = new();
    public ObservableCollection<AiModel> Models { get; } = new();
    public ObservableCollection<ReferenceItem> References { get; } = new();

    private ChatSession? _currentSession;
    public ChatSession? CurrentSession
    {
        get => _currentSession;
        private set { if (_currentSession != value) { _currentSession = value; Raise(nameof(CurrentSession)); } }
    }

    public HostClient? Host { get; private set; }

    private bool _isLoading = true;
    /// <summary>
    /// True until <see cref="StartHostAsync"/> finishes spawning the host,
    /// pulling models / sessions, and restoring the last-active session.
    /// The view shows a loading overlay while this is true so the user
    /// can't race the startup sequence by interacting before it's ready.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; Raise(nameof(IsLoading)); } }
    }

    private bool _isBusy;
    /// <summary>
    /// True between the user pressing Send and the turn ending (either
    /// TurnDone or an error). View disables the session picker, new /
    /// delete buttons, and the Send button while this is true so the
    /// user can't switch sessions mid-stream and corrupt routing. Cancel
    /// (Esc) stays available — it's how you exit the busy state.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); } }
    }

    private bool _hasUserInteracted;
    /// <summary>
    /// Flips true on the first user-driven action (typing in the input
    /// box, pinning a reference, picking a session manually). Once true,
    /// the background session loader will NOT auto-switch to the last-
    /// used session — assumes the user has already engaged with whatever
    /// state they landed in. Latches; never resets.
    /// </summary>
    public bool HasUserInteracted
    {
        get => _hasUserInteracted;
        private set { if (_hasUserInteracted != value) { _hasUserInteracted = value; Raise(nameof(HasUserInteracted)); } }
    }

    /// <summary>Mark a user-driven interaction. Idempotent (latches once true).</summary>
    public void MarkInteracted() => HasUserInteracted = true;

    /// <summary>Accumulated assistant text for the in-flight stream. Empty when no turn is active.</summary>
    public string StreamingText => _streamingText.ToString();

    /// <summary>Accumulated assistant thinking for the in-flight stream. Empty when no turn is active.</summary>
    public string StreamingThinking => _streamingThinkingText.ToString();

    /// <summary>Session id whose turn is currently streaming, or null when idle. View uses this to gate UI updates.</summary>
    public string? StreamingSessionId { get; private set; }

    /// <summary>Display label for the streaming bubble before the first onModelInfo notification arrives.</summary>
    public string InitialModelLabel { get; private set; } = "Claude";

    // ---- Internal state -----------------------------------------------

    private readonly StringBuilder _streamingText = new();
    private readonly StringBuilder _streamingThinkingText = new();
    private bool _refreshingSessions;
    private bool _refreshingModels;
    private string? _lastSyncedWorkspace;
    private bool _workspaceSynced;

    /// <summary>Set true while RefreshModels is repopulating; the view uses it to suppress its model-picker SelectionChanged side effects.</summary>
    public bool IsRefreshingModels => _refreshingModels;

    /// <summary>Set true while RefreshSessions is repopulating; the view uses it to suppress its session-picker SelectionChanged side effects.</summary>
    public bool IsRefreshingSessions => _refreshingSessions;

    // ---- Events the view renders --------------------------------------

    /// <summary>A session's history was just loaded. Carries the messages so the view can rebuild the bubble panel.</summary>
    public event Action<OpenSessionResult>? SessionLoaded;

    /// <summary>The user dropped to the home state (e.g. New Chat button). View clears history + shows hint.</summary>
    public event Action? HomeStateEntered;

    /// <summary>
    /// Fired once after <see cref="LoadSessionsInBackgroundAsync"/> finishes
    /// populating <see cref="Sessions"/>. The view uses this to (a) show the
    /// home recent-sessions list and (b) auto-restore the last-used session
    /// IF the user hasn't interacted yet.
    /// </summary>
    public event Action? SessionsLoaded;

    /// <summary>The user just sent a prompt. Carries the snapshot of references attached to it.</summary>
    public event Action<string, IReadOnlyList<ReferenceItem>>? UserMessageSent;

    /// <summary>An assistant streaming chunk arrived for the active session. View reads <see cref="StreamingText"/>.</summary>
    public event Action? AssistantStreamUpdated;

    /// <summary>A thinking streaming chunk arrived for the active session. View reads <see cref="StreamingThinking"/>.</summary>
    public event Action? ThinkingStreamUpdated;

    /// <summary>The model display name changed mid-stream (e.g. tool-use model swap).</summary>
    public event Action<string>? ModelInfoChanged;

    /// <summary>Token / cost usage reported for the active turn.</summary>
    public event Action<UsageParams>? UsageReceived;

    /// <summary>
    /// Streaming ended for a session. Args: sessionId, cancelled,
    /// wasActive (true when the ended session is the one the user is
    /// currently viewing — the view uses this to decide whether to
    /// update the status bar).
    /// </summary>
    public event Action<string, bool, bool>? StreamingEnded;

    /// <summary>An error to surface as an error bubble.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>A permission request landed; view renders an inline approval bubble.</summary>
    public event Action<PermissionRequestEvent>? PermissionRequested;

    // ---- Lifecycle ----------------------------------------------------

    /// <summary>Spawn the host process and run the initial workspace / model / session population.</summary>
    /// <param name="initialWorkspace">Solution directory at startup, or null if no solution is open.</param>
    public async Task StartHostAsync(string? initialWorkspace)
    {
        Host = await Task.Run(HostClient.Start);
        await UiThread.SwitchToUi();
        _lastSyncedWorkspace = initialWorkspace;
        _workspaceSynced = true;
        await Host.InitializeAsync(initialWorkspace);
        await UiThread.SwitchToUi();
        await RefreshModelsAsync();
        // Sessions are NOT loaded here — they happen in
        // LoadSessionsInBackgroundAsync after the view drops the loading
        // overlay. "Ready" = host alive + models populated + home shown.
        // Session list comes after; legacy chats can take a moment to
        // resolve (workspace wait, disk read) and shouldn't gate the UI.
    }

    /// <summary>
    /// Load the session list out-of-band, after the view has shown the
    /// home screen. Fires <see cref="SessionsLoaded"/> via
    /// <see cref="RefreshSessionsAsync"/> when done so the view can
    /// populate the home recent-list and (if the user hasn't engaged)
    /// restore the last-used session.
    /// </summary>
    public Task LoadSessionsInBackgroundAsync() => RefreshSessionsAsync();

    /// <summary>
    /// Hide the loading overlay. The view drives this — not
    /// <see cref="StartHostAsync"/> — because session-restore and
    /// the first history render happen after StartHostAsync returns.
    /// Calling this any earlier flashes a blank chat between "loading"
    /// and the first bubble appearing.
    /// </summary>
    public void MarkStartupComplete() => IsLoading = false;

    /// <summary>Best-effort sync wait so the in-flight draft gets persisted on tool-window close. View calls with the live InputBox text.</summary>
    public void DisposeHost(string finalDraftText, TimeSpan budget)
    {
#pragma warning disable VSTHRD002
        try
        {
            if (Host is not null && _currentSession is not null)
                Host.SetSessionDraftAsync(_currentSession.Id, finalDraftText).Wait(budget);
        }
        catch { }
#pragma warning restore VSTHRD002
        try { Host?.Dispose(); } catch { }
        Host = null;
    }

    // ---- Models / sessions / workspace --------------------------------

    public async Task RefreshModelsAsync()
    {
        if (Host is null) return;
        var models = await Host.ListModelsAsync();
        await UiThread.SwitchToUi();
        _refreshingModels = true;
        Models.Clear();
        foreach (var m in models)
            Models.Add(new AiModel { Id = m.Id, AdapterId = m.AdapterId, DisplayName = m.DisplayName, AdapterDisplayName = m.AdapterId });
        _refreshingModels = false;
    }

    public async Task RefreshSessionsAsync()
    {
        if (Host is null) return;
        var snapshot = await Host.ListSessionsAsync();
        await UiThread.SwitchToUi();
        _refreshingSessions = true;
        Sessions.Clear();
        foreach (var s in snapshot)
            Sessions.Add(new ChatSession
            {
                Id = s.Id,
                Label = string.IsNullOrEmpty(s.Label) ? "New chat" : s.Label,
                AdapterId = s.AdapterId,
                ModelId = s.ModelId,
                LastMessageAt = s.LastMessageAt,
            });
        _refreshingSessions = false;
        // Fire after every refresh — initial background load, workspace
        // switch, post-turn relabel — so the view can rebuild the home
        // recent-list and (if guards permit) auto-restore.
        SessionsLoaded?.Invoke();
    }

    /// <summary>Refresh just the dropdown labels — used after a turn so first-prompt-derived labels show up.</summary>
    public async Task RefreshSessionLabelsAsync()
    {
        if (Host is null) return;
        try
        {
            var snapshot = await Host.ListSessionsAsync();
            await UiThread.SwitchToUi();
            foreach (var s in snapshot)
            {
                var existing = Sessions.FirstOrDefault(x => x.Id == s.Id);
                if (existing is not null && !string.IsNullOrEmpty(s.Label)) existing.Label = s.Label;
            }
        }
        catch { }
    }

    /// <summary>Push the (possibly null) solution path to the host and re-pull sessions if it changed.</summary>
    public async Task SyncWorkspaceAsync(string? currentSolutionDir)
    {
        if (Host is null) return;
        if (_workspaceSynced && currentSolutionDir == _lastSyncedWorkspace) return;

        try { await Host.SetWorkspaceAsync(currentSolutionDir); } catch { }
        await UiThread.SwitchToUi();
        _lastSyncedWorkspace = currentSolutionDir;
        _workspaceSynced = true;

        await RefreshSessionsAsync();
        // Workspace changed and the previously-current session might no
        // longer be in this workspace's bucket — drop to home state so
        // the user explicitly picks or starts fresh.
        await UiThread.SwitchToUi();
        if (_currentSession is not null && !Sessions.Contains(_currentSession))
        {
            CurrentSession = null;
            HomeStateEntered?.Invoke();
        }
    }

    /// <summary>
    /// Switch active session to <paramref name="picked"/>. Persists the
    /// outgoing session's draft (passed in as <paramref name="leavingDraftText"/>).
    /// View receives the loaded history via <see cref="SessionLoaded"/>.
    /// </summary>
    public async Task LoadSelectedSessionAsync(ChatSession picked, string leavingDraftText)
    {
        if (Host is null) return;

        var leaving = _currentSession;
        if (leaving is not null && leaving != picked)
        {
            try { await Host.SetSessionDraftAsync(leaving.Id, leavingDraftText); } catch { }
        }
        CurrentSession = picked;

        try
        {
            var opened = await Host.OpenSessionAsync(picked.Id);
            await UiThread.SwitchToUi();
            picked.AdapterId = opened.AdapterId;
            picked.ModelId = opened.ModelId;
            SessionLoaded?.Invoke(opened);
        }
        catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); }
    }

    /// <summary>
    /// Drop back to the home state — no current session selected, the view
    /// renders the "send a message to start chatting" hint. Saves the
    /// outgoing session's draft so it's there when the user comes back.
    /// </summary>
    public async Task EnterHomeStateAsync(string leavingDraftText)
    {
        var leaving = _currentSession;
        if (leaving is not null && Host is not null)
        {
            try { await Host.SetSessionDraftAsync(leaving.Id, leavingDraftText); } catch { }
        }
        await UiThread.SwitchToUi();
        CurrentSession = null;
        HomeStateEntered?.Invoke();
    }

    /// <summary>
    /// Delete a session from the host and shift our local id mapping.
    /// The host uses integer indices keyed by position in the bucket,
    /// so deleting one shifts every later session's id down by 1.
    /// </summary>
    public async Task DeleteSessionAsync(ChatSession sess)
    {
        if (Host is null) return;
        if (!int.TryParse(sess.Id, out var deletedIdx)) return;
        try { await Host.DeleteSessionAsync(sess.Id); } catch { return; }
        await UiThread.SwitchToUi();
        foreach (var s in Sessions)
        {
            if (s != sess && int.TryParse(s.Id, out var sIdx) && sIdx > deletedIdx)
                s.Id = (sIdx - 1).ToString();
        }
        Sessions.Remove(sess);
    }

    /// <summary>Update the current session's adapter / model when the model picker changes (skipped during refresh).</summary>
    public void OnModelPicked(AiModel model)
    {
        if (_refreshingModels || _currentSession is null) return;
        _currentSession.AdapterId = model.AdapterId;
        _currentSession.ModelId = model.Id;
    }

    // ---- Send / cancel ------------------------------------------------

    /// <summary>
    /// Build the prompt payload, fire <see cref="UserMessageSent"/> so
    /// the view appends the user bubble + opens a streaming bubble,
    /// then dispatch to the host. Returns false if no model is picked
    /// (caller surfaces a status hint).
    /// </summary>
    public async Task<bool> SendAsync(string text, string? currentSolutionDir, AiModel? model)
    {
        if (Host is null) return false;
        if (model is null) return false;
        if (string.IsNullOrEmpty(text)) return false;
        // Already streaming a turn — Send button should be disabled but
        // belt-and-suspenders.
        if (IsBusy) return false;

        await SyncWorkspaceAsync(currentSolutionDir);

        // Home state: this is the first message — create a session
        // just-in-time so the host has somewhere to persist the turn.
        // We add it to the dropdown locally so the user sees it
        // immediately; the host's listSessions filters empties so on
        // any future refresh (after PersistTurn) the same entry is
        // returned with content. If the send itself fails before
        // PersistTurn runs, we fall back to clearing the orphan below.
        if (_currentSession is null)
        {
            try
            {
                var created = await Host.OpenSessionAsync(sessionId: null);
                await UiThread.SwitchToUi();
                var s = new ChatSession
                {
                    Id = created.SessionId,
                    Label = "New chat",
                    AdapterId = created.AdapterId,
                    ModelId = created.ModelId,
                };
                Sessions.Add(s);
                CurrentSession = s;
            }
            catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); return false; }
        }

        ExtensionSettings? settings = null;
        try { settings = await Host.GetSettingsAsync(); } catch { }
        await UiThread.SwitchToUi();

        var refsSnapshot = References.ToList();
        var autoRef = TryBuildActiveFileReference(refsSnapshot, settings);
        if (autoRef is not null) refsSnapshot.Add(autoRef);

        // Update the session label locally to the first-prompt snippet
        // so the dropdown reflects the chat title immediately. The host
        // does the same write inside PersistTurn; the post-turn label
        // refresh just reaffirms the same value.
        if (_currentSession is not null
            && (_currentSession.Label == "New chat" || string.IsNullOrEmpty(_currentSession.Label)))
        {
            _currentSession.Label = text.Length <= 40 ? text : text.Substring(0, 40);
        }

        UserMessageSent?.Invoke(text, refsSnapshot);

        // Reset streaming accumulators for the new turn.
        StreamingSessionId = _currentSession!.Id;
        InitialModelLabel = model.DisplayName;
        _streamingText.Clear();
        _streamingThinkingText.Clear();
        IsBusy = true;

        References.Clear();

        var protocolRefs = ToProtocolReferences(refsSnapshot);
        try
        {
            await Host.SendPromptAsync(new SendPromptParams(
                _currentSession.Id, model.AdapterId, model.Id, text, protocolRefs));
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            // The host will never send TurnDone for an inline failure;
            // surface it ourselves. wasActive=true because we just sent
            // from the currently-displayed session.
            var sid = _currentSession.Id;
            StreamingSessionId = null;
            _streamingText.Clear();
            _streamingThinkingText.Clear();
            IsBusy = false;
            StreamingEnded?.Invoke(sid, false, true);
            return false;
        }
    }

    public async Task CancelAsync()
    {
        if (Host is null || _currentSession is null) return;
        try { await Host.CancelTurnAsync(_currentSession.Id); } catch { }
    }

    // ---- Host event ingest (called by view from OnUi wrappers) --------

    /// <summary>True when a chunk's session id matches the active stream AND the user hasn't switched away.</summary>
    public bool IsForActiveStream(string sessionId)
        => sessionId == StreamingSessionId && _currentSession?.Id == StreamingSessionId;

    public void OnAssistantChunk(string sessionId, string text)
    {
        if (!IsForActiveStream(sessionId)) return;
        _streamingText.Append(text);
        AssistantStreamUpdated?.Invoke();
    }

    public void OnThinkingChunk(string sessionId, string text)
    {
        if (!IsForActiveStream(sessionId)) return;
        _streamingThinkingText.Append(text);
        ThinkingStreamUpdated?.Invoke();
    }

    public void OnModelInfo(string sessionId, string modelDisplayName)
    {
        if (!IsForActiveStream(sessionId)) return;
        // Update InitialModelLabel too — the Claude CLI's `system/init`
        // event typically arrives BEFORE the first content chunk, so the
        // streaming bubble doesn't exist yet. Updating the label-source
        // here means EnsureStreamingBubble (called on first chunk) picks
        // up the concrete model name instead of the picker label
        // ("Default" / "Sonnet").
        InitialModelLabel = modelDisplayName;
        ModelInfoChanged?.Invoke(modelDisplayName);
    }

    public void OnUsage(UsageParams p)
    {
        if (!IsForActiveStream(p.SessionId)) return;
        UsageReceived?.Invoke(p);
    }

    public async void OnTurnDone(string sessionId, bool cancelled)
    {
        // Capture wasActive before we clear StreamingSessionId — the view
        // uses it to decide whether the status bar should reflect this end.
        var wasActive = IsForActiveStream(sessionId);
        if (sessionId == StreamingSessionId)
        {
            StreamingSessionId = null;
            _streamingText.Clear();
            _streamingThinkingText.Clear();
            IsBusy = false;
            StreamingEnded?.Invoke(sessionId, cancelled, wasActive);
        }
        // If the TurnDone is for a session we're not tracking (rare —
        // happens if a user-cancel races a natural completion), drop it
        // silently. The view is already cleaned up.
        await RefreshSessionLabelsAsync();
    }

    public void OnError(string message) => ErrorOccurred?.Invoke(message);
    public void OnPermissionRequest(PermissionRequestEvent p) => PermissionRequested?.Invoke(p);

    // ---- Reference handling -------------------------------------------

    /// <summary>Add or merge a reference (called by the SendSelection / AddFile commands).</summary>
    public void AppendReference(string displayPath, string absolutePath, int startLine, int endLine, string content)
    {
        var existing = References.FirstOrDefault(r =>
            string.Equals(r.AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase));
        bool isWholeFile = startLine <= 0;

        if (existing is not null)
        {
            if (existing.IsWholeFile) return;
            if (isWholeFile)
            {
                existing.Ranges.Clear();
                existing.FullContent = content;
            }
            else
            {
                existing.Ranges.Add(new LineRange { Start = startLine, End = endLine, Body = content });
                existing.MergeOverlappingRanges();
            }
            existing.NotifyChanged();
            return;
        }

        var item = new ReferenceItem { FilePath = displayPath, AbsolutePath = absolutePath };
        if (isWholeFile) item.FullContent = content;
        else item.Ranges.Add(new LineRange { Start = startLine, End = endLine, Body = content });
        References.Add(item);
    }

    // ---- Pure helpers -------------------------------------------------

    static ReferenceItem? TryBuildActiveFileReference(IList<ReferenceItem> current, ExtensionSettings? settings)
    {
        if (settings?.General.AutoAttachActiveFile != true) return null;
        try
        {
            var path = Editor.EditorSelectionService.GetActiveDocumentPath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            if (current.Any(r => string.Equals(r.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))) return null;
            return new ReferenceItem
            {
                FilePath = Editor.EditorSelectionService.MakeClaudeFilePath(path) ?? string.Empty,
                AbsolutePath = path!,
                FullContent = System.IO.File.ReadAllText(path!),
            };
        }
        catch { return null; }
    }

    static IReadOnlyList<Reference>? ToProtocolReferences(IList<ReferenceItem> items)
    {
        if (items.Count == 0) return null;
        var list = new List<Reference>(items.Count);
        foreach (var r in items)
        {
            list.Add(r.IsWholeFile
                ? new Reference(r.AbsolutePath, r.FullContent, null)
                : new Reference(r.AbsolutePath, null,
                    r.Ranges.Select(rng => new ReferenceRange(rng.Start, rng.End)).ToList()));
        }
        return list;
    }

    void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
