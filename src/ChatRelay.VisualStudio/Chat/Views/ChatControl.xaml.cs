using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ChatRelay.Chat.Models;
using ChatRelay.Chat.Rendering;
using ChatRelay.Chat.ViewModels;
using ChatRelay.Host;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ChatRelay.Chat.Views;

/// <summary>
/// Pure rendering tier for the chat tool window. Owns all WPF imperative
/// bubble building (user / assistant / error / permission), streaming
/// bubble UI elements, and the reference-chip strip. State and host
/// communication live on <see cref="ChatViewModel"/> — this code-behind
/// only listens for VM events and renders.
/// </summary>
public partial class ChatControl : UserControl
{
    static readonly Brush UserBubbleBg = Frozen(Color.FromArgb(90, 59, 130, 246));
    static readonly Brush AssistantBubbleBg = Frozen(Color.FromArgb(20, 128, 128, 128));
    static readonly Brush ErrorBubbleBg = Frozen(Color.FromArgb(40, 220, 80, 80));
    static readonly Brush Accent = Frozen(Color.FromRgb(0x40, 0xE0, 0xD0));
    static readonly Brush RangeBlue = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));

    // Maximum time we'll block the UI thread waiting for the in-flight
    // draft-save RPC during VS shutdown. Long enough for the host's stdio
    // round-trip on a healthy machine, short enough that a stuck host
    // can't visibly hang VS. Drafts are also saved on every session
    // switch + send, so this only catches "typed something then closed
    // VS without sending."
    static readonly TimeSpan DraftSaveOnShutdownBudget = TimeSpan.FromMilliseconds(500);

    readonly ChatViewModel _vm = new();
    readonly MarkdownRenderer _markdown;

    // Streaming bubble UI element refs — purely view-local. Cleared when
    // the VM signals StreamingEnded.
    StackPanel? _streamingStack;
    TextBlock? _streamingLabelTb;
    FrameworkElement? _streamingMarkdownView;
    TextBlock? _streamingThinkingBlock;
    TextBlock? _streamingFooter;
    Border? _thinkingBubble;
    // Index into _vm.StreamingText where the *current* streaming bubble
    // begins. Bumped when we split (e.g. a permission bubble lands mid-
    // stream and we want any further chunks to land in a fresh bubble
    // below it). Reset to 0 on StreamingEnded.
    int _streamingSplitOffset;
    // Same idea for thinking: each "burst" of reasoning chunks renders into
    // its own separate expander outside the assistant bubble. When assistant
    // text arrives we close out the burst; the next thinking chunk starts a
    // fresh one. Offset tracks how much of _vm.StreamingThinking the *prior*
    // bursts already absorbed.
    int _thinkingSplitOffset;
    // Active tool-call status lines, keyed by adapter call id (or a synthetic
    // fallback when the adapter didn't supply one). Lets the Completed phase
    // update the same line we wrote on Requested. Cleared on session switch
    // and after the streaming turn ends.
    readonly Dictionary<string, TextBlock> _toolCallLines = new();
    // Keys (toolName|inputJson) the user explicitly denied via a permission
    // bubble. Consumed once when the matching Failed tool-call event arrives
    // so it can render as a quiet "× skipped" instead of a red "⚠ failed".
    readonly HashSet<string> _recentlyDeniedKeys = new();
    System.Windows.Threading.DispatcherTimer? _idleDotsTimer;

    EnvDTE.SolutionEvents? _solutionEvents;

    // Set true while WE assign SessionBox.SelectedItem so the
    // SelectionChanged handler can distinguish that from a user-driven
    // pick (the latter counts as an interaction; the former doesn't).
    bool _settingSelectedItemProgrammatically;

    public ChatControl()
    {
        InitializeComponent();
        _markdown = new MarkdownRenderer(this);

        SessionBox.ItemsSource = _vm.Sessions;
        var modelsView = new ListCollectionView(_vm.Models)
        {
            GroupDescriptions = { new PropertyGroupDescription(nameof(AiModel.AdapterDisplayName)) }
        };
        ModelBox.ItemsSource = modelsView;

        _vm.References.CollectionChanged += OnReferencesChanged;
        _vm.Proposals.CollectionChanged += OnProposalsChanged;
        _vm.Denials.CollectionChanged += OnDenialsChanged;
        // Re-pick the model selector when the model list changes (initial
        // populate, or Ollama coming online later, etc.) so we don't sit
        // with an empty dropdown.
        _vm.Models.CollectionChanged += (_, _) => RestoreModelSelection();
        // Rebuild the home recent-list whenever the session list changes.
        // Skip during a bulk refresh — RefreshSessionsAsync emits
        // SessionsLoaded at the end, which rebuilds once. Single Adds
        // (e.g. send-from-home creates a new session) rebuild immediately.
        _vm.Sessions.CollectionChanged += (_, _) =>
        {
            if (!_vm.IsRefreshingSessions) RebuildHomeRecentList();
        };
        WireVmEvents();
        HookSolutionEvents();

        ThreadHelper.JoinableTaskFactory.RunAsync(StartHostAsync).FileAndForget("chatrelay/shell/start");
    }

    void WireVmEvents()
    {
        _vm.SessionLoaded += OnVmSessionLoaded;
        _vm.HomeStateEntered += OnVmHomeStateEntered;
        _vm.UserMessageSent += OnVmUserMessageSent;
        _vm.AssistantStreamUpdated += OnVmAssistantStreamUpdated;
        _vm.ThinkingStreamUpdated += OnVmThinkingStreamUpdated;
        _vm.ModelInfoChanged += OnVmModelInfoChanged;
        _vm.UsageReceived += OnVmUsageReceived;
        _vm.StreamingEnded += OnVmStreamingEnded;
        _vm.ErrorOccurred += AppendErrorBubble;
        _vm.PermissionRequested += AppendPermissionBubble;
        _vm.ToolCallObserved += OnVmToolCallObserved;
        _vm.SessionsLoaded += OnVmSessionsLoaded;
        _vm.ProposalsBecameNonEmpty += OnVmProposalsBecameNonEmpty;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsLoading):
                LoadingOverlay.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(ChatViewModel.CurrentSession):
                HomePane.Visibility = _vm.CurrentSession is null ? Visibility.Visible : Visibility.Collapsed;
                // Keep the dropdown selection in lockstep with the VM. The
                // home → send path creates a session and sets CurrentSession
                // without touching SessionBox; without this sync the dropdown
                // would stay blank while the chat shows the new session.
                // SelectionChanged guards against re-entry (picked == current).
                if (!ReferenceEquals(SessionBox.SelectedItem, _vm.CurrentSession))
                {
                    _settingSelectedItemProgrammatically = true;
                    SessionBox.SelectedItem = _vm.CurrentSession;
                    _settingSelectedItemProgrammatically = false;
                }
                break;
            case nameof(ChatViewModel.IsBusy):
                // Session controls lock during a streaming turn so a
                // mid-stream switch can't corrupt routing. The send button
                // stays enabled and flips into a stop button — clicking it
                // (or pressing Esc) cancels the turn.
                var enabled = !_vm.IsBusy;
                SessionBox.IsEnabled = enabled;
                NewSessionButton.IsEnabled = enabled;
                DeleteSessionButton.IsEnabled = enabled;
                SendButton.Content = _vm.IsBusy ? "■" : "⏎";
                SendButton.ToolTip = _vm.IsBusy ? "Stop (Esc)" : "Send (Enter)";
                break;
            case nameof(ChatViewModel.OpenLinesAdded):
            case nameof(ChatViewModel.OpenLinesRemoved):
            case nameof(ChatViewModel.AcceptedLinesAdded):
            case nameof(ChatViewModel.AcceptedLinesRemoved):
                UpdateChangesHeader();
                break;
        }
    }

    void UpdateChangesHeader()
    {
        var sb = new System.Text.StringBuilder();
        if (_vm.OpenLinesAdded > 0 || _vm.OpenLinesRemoved > 0)
            sb.Append($"open +{_vm.OpenLinesAdded} −{_vm.OpenLinesRemoved}");
        if (_vm.AcceptedLinesAdded > 0 || _vm.AcceptedLinesRemoved > 0)
        {
            if (sb.Length > 0) sb.Append("  ·  ");
            sb.Append($"accepted +{_vm.AcceptedLinesAdded} −{_vm.AcceptedLinesRemoved}");
        }
        ChangesHeaderCounters.Text = sb.ToString();
        ChangesHeader.Visibility = sb.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task StartHostAsync()
    {
        try
        {
            // Workspace probe doesn't gate the loading overlay anymore —
            // it's fast when a solution is already loaded, slow only on
            // auto-restore-before-solution-load. Either way, sessions
            // come in the background.
            var workspace = await ResolveInitialWorkspaceAsync();
            await _vm.StartHostAsync(workspace);

            // Bridge background-thread host events to UI-thread VM calls.
            var host = _vm.Host!;
            host.AssistantChunk    += p => UiThread.OnUi(() => _vm.OnAssistantChunk(p.SessionId, p.Text));
            host.ThinkingChunk     += p => UiThread.OnUi(() => _vm.OnThinkingChunk(p.SessionId, p.Text));
            host.ModelInfo         += p => UiThread.OnUi(() => _vm.OnModelInfo(p.SessionId, p.ModelDisplayName));
            host.Usage             += p => UiThread.OnUi(() => _vm.OnUsage(p));
            host.Error             += p => UiThread.OnUi(() => _vm.OnError(p.Message));
            host.TurnDone          += p => UiThread.OnUi(() => _vm.OnTurnDone(p.SessionId, p.Cancelled));
            host.PermissionRequest += p => UiThread.OnUi(() => _vm.OnPermissionRequest(p));
            host.ToolCall          += p => UiThread.OnUi(() => _vm.OnToolCall(p));
            host.AdaptersChanged   += () => UiThread.OnUi(() => _ = _vm.RefreshModelsAsync());
            host.ModelsChanged     += () => UiThread.OnUi(() => _ = _vm.RefreshModelsAsync());
            host.ChangesUpdated    += s  => UiThread.OnUi(() => _vm.OnChangesUpdated(s));

            // Show the home screen and drop the loading overlay now —
            // models are loaded, host is responsive, the chat is usable.
            // The session list comes in via OnVmSessionsLoaded after a
            // background fetch.
            await UiThread.SwitchToUi();
            HomePane.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            await UiThread.SwitchToUi();
            AppendErrorBubble("Host failed to start: " + ex.Message);
        }
        finally
        {
            await UiThread.SwitchToUi();
            _vm.MarkStartupComplete();
        }

        // Background session load — overlay is already gone. Errors here
        // surface as an in-chat error bubble; user can continue.
        try { await _vm.LoadSessionsInBackgroundAsync(); }
        catch (Exception ex)
        {
            await UiThread.SwitchToUi();
            AppendErrorBubble("Failed to load sessions: " + ex.Message);
        }
    }

    void OnVmSessionsLoaded()
    {
        // Always populate the home recent-list (visible only when in home).
        RebuildHomeRecentList();

        // Auto-restore the most recently used session if the user hasn't
        // engaged with the extension yet. Sessions are sorted newest-
        // first by host-side LastMessageAt, so Sessions[0] is the target.
        if (_vm.HasUserInteracted) return;
        if (_vm.Sessions.Count == 0) return;
        if (_vm.CurrentSession is not null) return; // already on a session
        var toOpen = _vm.Sessions[0];
        // Fire-and-forget (FileAndForget for proper VSIX exception capture).
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await _vm.LoadSelectedSessionAsync(toOpen, string.Empty);
        }).FileAndForget("chatrelay/shell/auto-restore");
    }

    // ---- Session / model picker handlers ---------------------------------

    async void SessionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.IsRefreshingSessions) return;
        if (SessionBox.SelectedItem is not ChatSession picked) return;
        // Skip when startup already loaded this session — avoids a
        // duplicate render when SelectedItem is set after the initial
        // explicit Load.
        if (picked == _vm.CurrentSession) return;
        // User-driven pick (not a programmatic CurrentSession-sync) — count
        // it as an interaction so the background loader doesn't
        // auto-redirect them.
        if (!_settingSelectedItemProgrammatically) _vm.MarkInteracted();
        await _vm.LoadSelectedSessionAsync(picked, InputBox.Text);
    }

    void OnVmSessionLoaded(OpenSessionResult opened)
    {
        HistoryPanel.Children.Clear();
        _toolCallLines.Clear();
        _recentlyDeniedKeys.Clear();
        foreach (var m in opened.Messages)
        {
            if (m.Role == "user") AppendUserBubble(m.Text, references: null, timestamp: m.Timestamp);
            else AppendAssistantBubbleStatic(m.Text, m.Thinking, m.Usage, m.Model, m.Timestamp, m.Cancelled);
        }
        HistoryScroll.ScrollToEnd();
        InputBox.Text = opened.DraftText ?? string.Empty;
        RestoreModelSelection();
    }

    // Up to 5 most-recent sessions, clickable, shown under the home hint.
    // Sessions are already sorted newest-first by the host's ListSessions.
    const int HomeRecentMax = 5;

    void RebuildHomeRecentList()
    {
        HomeRecentList.Children.Clear();
        if (_vm.Sessions.Count == 0)
        {
            HomeRecentHeader.Visibility = Visibility.Collapsed;
            return;
        }
        SessionLoadingHint.Visibility = Visibility.Collapsed;
        HomeRecentHeader.Visibility = Visibility.Visible;
        var top = _vm.Sessions.Take(HomeRecentMax);
        foreach (var s in top)
            HomeRecentList.Children.Add(BuildHomeRecentEntry(s));
    }

    FrameworkElement BuildHomeRecentEntry(ChatSession s)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var link = new TextBlock { Cursor = Cursors.Hand, FontSize = 12 };
        var hyperlink = new Hyperlink(new Run(s.Label)) { TextDecorations = TextDecorations.Underline };
        hyperlink.Foreground = Accent;
        hyperlink.Click += async (_, _) =>
        {
            _vm.MarkInteracted();
            _settingSelectedItemProgrammatically = true;
            SessionBox.SelectedItem = s;
            _settingSelectedItemProgrammatically = false;
            await _vm.LoadSelectedSessionAsync(s, InputBox.Text);
        };
        link.Inlines.Add(hyperlink);
        stack.Children.Add(link);

        if (s.LastMessageAt is { } ts)
        {
            var when = new TextBlock
            {
                Text = "  " + FormatTimestamp(ts),
                FontSize = 11,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            when.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            stack.Children.Add(when);
        }
        return stack;
    }

    // Locale-aware bubble / list timestamp:
    //   same calendar day → short time only ("3:42 PM" / "15:42")
    //   different day     → short date + short time ("4/29/2026 3:42 PM")
    // Both use the current culture's short patterns.
    static string FormatTimestamp(DateTime utcOrLocal)
    {
        var local = utcOrLocal.Kind == DateTimeKind.Utc
            ? utcOrLocal.ToLocalTime()
            : utcOrLocal;
        return local.Date == DateTime.Now.Date
            ? local.ToString("t")
            : local.ToString("g");
    }

    void OnVmHomeStateEntered()
    {
        // Home state: blank history, empty input. HomePane visibility is
        // driven by the CurrentSession PropertyChanged handler.
        HistoryPanel.Children.Clear();
        InputBox.Clear();
        SetStatus(string.Empty);
    }

    // Pick the model dropdown's selection in priority order:
    //   1. Whatever the active session has saved (so resuming a chat
    //      brings back the model the user last used on it).
    //   2. The first model in the list — only if nothing is selected
    //      yet (avoids stomping on a user's manual choice).
    // Called after the session loads and after the Models collection
    // changes (initial populate, Ollama coming online, …).
    void RestoreModelSelection()
    {
        if (_vm.CurrentSession?.ModelId is { Length: > 0 } mid)
        {
            var saved = _vm.Models.FirstOrDefault(
                x => x.Id == mid && x.AdapterId == _vm.CurrentSession.AdapterId);
            if (saved is not null) { ModelBox.SelectedItem = saved; return; }
        }
        if (ModelBox.SelectedItem is null && _vm.Models.Count > 0)
            ModelBox.SelectedIndex = 0;
    }

    async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.MarkInteracted();
        // No more empty-placeholder sessions. New Chat just drops back
        // to the home state; the next Send creates a real session.
        _settingSelectedItemProgrammatically = true;
        SessionBox.SelectedItem = null;
        _settingSelectedItemProgrammatically = false;
        await _vm.EnterHomeStateAsync(InputBox.Text);
    }

    async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentSession is null) return;
        _vm.MarkInteracted();
        var toDelete = _vm.CurrentSession;
        HistoryPanel.Children.Clear();
        await _vm.DeleteSessionAsync(toDelete);
        // After deletion: drop to home state. Don't auto-select anything.
        _settingSelectedItemProgrammatically = true;
        SessionBox.SelectedItem = null;
        _settingSelectedItemProgrammatically = false;
        await _vm.EnterHomeStateAsync(string.Empty);
    }

    void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is AiModel m) _vm.OnModelPicked(m);
    }

    // ---- Input + send ----------------------------------------------------

    void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Any key in the input box counts as the user engaging — don't
        // let the background session loader yank them to a different
        // session afterwards.
        _vm.MarkInteracted();

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            // Enter during a stream is a typing event — don't fire the
            // button (which would now cancel). Esc is the keybind for stop.
            if (_vm.IsBusy) return;
            SendButton_Click(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = _vm.CancelAsync();
        }
    }

    async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) { await _vm.CancelAsync(); return; }
        if (ModelBox.SelectedItem is not AiModel model) { SetStatus("Pick a model first."); return; }
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        InputBox.Clear();
        SetStatus("…");
        var workspace = Editor.EditorSelectionService.GetSolutionDirectory();
        await _vm.SendAsync(text, workspace, model);
    }

    // ---- VM event handlers (rendering) -----------------------------------

    void OnVmUserMessageSent(string text, IReadOnlyList<ReferenceItem> refs)
    {
        AppendUserBubble(text, refs, DateTime.Now);
        // Reset streaming UI fields so the next chunk lazily creates a fresh
        // bubble; show thinking dots speculatively until the first chunk lands.
        _streamingStack = null;
        _streamingLabelTb = null;
        _streamingMarkdownView = null;
        _streamingThinkingBlock = null;
        _streamingFooter = null;
        _streamingSplitOffset = 0;
        _thinkingSplitOffset = 0;
        ShowThinkingDots();
    }

    /// <summary>
    /// Close out the in-flight streaming bubble in place and reset the
    /// streaming refs so any subsequent assistant chunk creates a fresh
    /// bubble below. Used when an intermediate (permission / error)
    /// bubble lands mid-stream — keeps the visual continuity readable
    /// instead of growing the same bubble around the interruption.
    /// </summary>
    void SplitStreamingBubble()
    {
        if (_streamingStack is null) return;
        // Snapshot how much of the accumulator the *previous* bubble has
        // already rendered. New bubble's markdown will start from here.
        _streamingSplitOffset = _vm.StreamingText.Length;
        _streamingStack = null;
        _streamingLabelTb = null;
        _streamingMarkdownView = null;
        _streamingThinkingBlock = null;
        _streamingFooter = null;
    }

    void OnVmAssistantStreamUpdated()
    {
        HideThinkingDots();
        RestartIdleDotsTimer();
        // Assistant text after a thinking burst closes that burst and starts
        // a fresh assistant bubble below it. Subsequent thinking would then
        // open a new expander, keeping the visual rhythm: thought · reply ·
        // thought · reply.
        if (_streamingThinkingBlock is not null)
        {
            _thinkingSplitOffset = _vm.StreamingThinking.Length;
            _streamingThinkingBlock = null;
            SplitStreamingBubble();
        }
        var stack = EnsureStreamingBubble();
        var full = _vm.StreamingText;
        var slice = _streamingSplitOffset > 0 && _streamingSplitOffset <= full.Length
            ? full.Substring(_streamingSplitOffset)
            : full;
        var rebuilt = _markdown.Build(slice);
        if (_streamingMarkdownView is null)
        {
            var insertAt = stack.Children.Count;
            if (_streamingFooter is not null) insertAt = stack.Children.IndexOf(_streamingFooter);
            stack.Children.Insert(insertAt, rebuilt);
        }
        else
        {
            var idx = stack.Children.IndexOf(_streamingMarkdownView);
            stack.Children.RemoveAt(idx);
            stack.Children.Insert(idx, rebuilt);
        }
        _streamingMarkdownView = rebuilt;
        HistoryScroll.ScrollToEnd();
    }

    void OnVmThinkingStreamUpdated()
    {
        HideThinkingDots();
        RestartIdleDotsTimer();
        // First chunk of a new burst: split any active assistant bubble so
        // the expander lands between the prior bubble and any later text,
        // then add the expander as its own row in the chat history.
        if (_streamingThinkingBlock is null)
        {
            SplitStreamingBubble();
            _streamingThinkingBlock = ThinkingBlock();
            HistoryPanel.Children.Add(ThinkingExpander(_streamingThinkingBlock));
        }
        var full = _vm.StreamingThinking;
        var slice = _thinkingSplitOffset > 0 && _thinkingSplitOffset <= full.Length
            ? full.Substring(_thinkingSplitOffset)
            : full;
        _streamingThinkingBlock.Text = slice;
        HistoryScroll.ScrollToEnd();
    }

    void OnVmModelInfoChanged(string modelDisplayName)
    {
        if (_streamingLabelTb is not null) _streamingLabelTb.Text = modelDisplayName;
    }

    void OnVmUsageReceived(UsageParams p)
    {
        var stack = EnsureStreamingBubble();
        if (_streamingFooter is null)
        {
            _streamingFooter = FooterBlock();
            stack.Children.Add(_streamingFooter);
        }
        _streamingFooter.Text = FormatUsage(p);
    }

    void OnVmStreamingEnded(string sessionId, bool cancelled, bool wasActive)
    {
        StopIdleDotsTimer();
        HideThinkingDots();
        // Append a timestamp footer to the just-finished assistant bubble
        // so streamed turns match the historical render. On cancel, replace
        // with a "(cancelled by user)" marker so the in-place bubble matches
        // what the next session reload will show.
        if (_streamingStack is not null)
        {
            if (cancelled) _streamingStack.Children.Add(BuildCancelledFooter());
            else _streamingStack.Children.Add(BuildTimestampFooter(DateTime.Now));
        }
        _streamingStack = null;
        _streamingLabelTb = null;
        _streamingMarkdownView = null;
        _streamingThinkingBlock = null;
        _streamingFooter = null;
        _streamingSplitOffset = 0;
        _thinkingSplitOffset = 0;
        _toolCallLines.Clear();
        _recentlyDeniedKeys.Clear();
        if (wasActive)
        {
            if (cancelled) SetStatus("Cancelled.");
            else SetStatus(string.Empty);
        }
    }

    // Initial-workspace probe with a bounded wait. If a solution is
    // associated but not yet fully loaded (typical when VS restores the
    // chat tool window on launch), poll until the directory becomes
    // available or we hit the timeout. Falls back to null — the
    // OnSolutionChanged handler still picks it up later if needed.
    static readonly TimeSpan WorkspaceLoadBudget = TimeSpan.FromSeconds(15);

    async Task<string?> ResolveInitialWorkspaceAsync()
    {
        await UiThread.SwitchToUi();
        var dir = Editor.EditorSelectionService.GetSolutionDirectory();
        if (!string.IsNullOrEmpty(dir)) return dir;

        // No directory yet — but is one coming? DTE.Solution.FullName is
        // set as soon as VS knows about the .sln, even before projects
        // finish loading. If empty, there really is no solution.
        string? slnPath = null;
        try
        {
            var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
            slnPath = dte?.Solution?.FullName;
        }
        catch { }
        if (string.IsNullOrEmpty(slnPath)) return null;

        var deadline = DateTime.UtcNow + WorkspaceLoadBudget;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            await UiThread.SwitchToUi();
            dir = Editor.EditorSelectionService.GetSolutionDirectory();
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return null;
    }

    // ---- Solution events -------------------------------------------------

    void HookSolutionEvents()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
            if (dte is null) return;
            // Holding the events object alive prevents the DTE from collecting
            // it (DTE event objects are weakly held; lose the reference and
            // events stop firing).
            _solutionEvents = dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionChanged;
            _solutionEvents.AfterClosing += OnSolutionChanged;
        }
        catch { }
    }

    void OnSolutionChanged() =>
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await UiThread.SwitchToUi();
            string? current = null;
            try { current = Editor.EditorSelectionService.GetSolutionDirectory(); } catch { }
            await _vm.SyncWorkspaceAsync(current);
        }).FileAndForget("chatrelay/shell/solution");

    // ---- Bubble building -------------------------------------------------

    void AppendUserBubble(string text, IReadOnlyList<ReferenceItem>? references, DateTime? timestamp = null)
    {
        var stack = new StackPanel();
        if (references is { Count: > 0 })
            stack.Children.Add(BuildBubbleReferencesExpander(references));
        AppendUserTextParts(stack, text);
        if (timestamp is { } ts) stack.Children.Add(BuildTimestampFooter(ts));
        HistoryPanel.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 8, 0, 8),
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(4),
            Background = UserBubbleBg,
            Child = stack,
        });
        HistoryScroll.ScrollToEnd();
    }

    static TextBlock BuildCancelledFooter()
    {
        var tb = new TextBlock
        {
            Text = "(cancelled by user)",
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return tb;
    }

    static TextBlock BuildTimestampFooter(DateTime ts)
    {
        var tb = new TextBlock
        {
            Text = FormatTimestamp(ts),
            FontSize = 10,
            Opacity = 0.45,
            Margin = new Thickness(0, 4, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return tb;
    }

    Expander BuildBubbleReferencesExpander(IReadOnlyList<ReferenceItem> references)
    {
        var chips = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var r in references) chips.Children.Add(CreateReferenceChip(r, interactive: false));
        var expander = new Expander
        {
            Header = $"References ({references.Count})",
            IsExpanded = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Content = chips,
        };
        expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return expander;
    }

    void AppendAssistantBubbleStatic(string text, string? thinking, UsagePayload? usage, string? model, DateTime? timestamp = null, bool cancelled = false)
    {
        // Thinking renders as its own small unboxed expander above the
        // assistant bubble, matching the live streaming layout.
        if (!string.IsNullOrEmpty(thinking))
        {
            var tb = ThinkingBlock();
            tb.Text = thinking!;
            HistoryPanel.Children.Add(ThinkingExpander(tb));
        }
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(model) ? "Claude" : model,
            FontWeight = FontWeights.Bold,
            Foreground = Accent,
        });
        stack.Children.Add(_markdown.Build(text));
        if (usage is not null)
        {
            var footer = FooterBlock();
            footer.Text = FormatUsage(usage);
            stack.Children.Add(footer);
        }
        if (cancelled) stack.Children.Add(BuildCancelledFooter());
        if (timestamp is { } ts) stack.Children.Add(BuildTimestampFooter(ts));
        HistoryPanel.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Background = AssistantBubbleBg,
            Child = stack,
        });
        HistoryScroll.ScrollToEnd();
    }

    void AppendErrorBubble(string message)
    {
        SplitStreamingBubble();
        var tb = new TextBlock { Text = "Error: " + message, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        HistoryPanel.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Background = ErrorBubbleBg,
            Child = tb,
        });
        HistoryScroll.ScrollToEnd();
    }

    void AppendPermissionBubble(PermissionRequestEvent p)
    {
        SplitStreamingBubble();

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = "🔐 Permission requested: " + p.ToolName,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        stack.Children.Add(header);

        var preview = new TextBox
        {
            Text = PrettyJson(p.InputJson),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(6),
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
        };
        preview.SetResourceReference(TextBox.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
        preview.SetResourceReference(TextBox.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        stack.Children.Add(preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var deny = PermissionButton("Deny", "PermissionDenyButtonStyle");
        var once = PermissionButton("Allow once", "PermissionAllowOnceButtonStyle");
        var always = PermissionButton("Allow always", "PermissionAllowAlwaysButtonStyle");
        buttons.Children.Add(deny);
        buttons.Children.Add(once);
        buttons.Children.Add(always);
        stack.Children.Add(buttons);

        // Same styling as the assistant bubble — the internal layout (header
        // + JSON preview + action buttons) already signals "this is different".
        var bubble = new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Background = AssistantBubbleBg,
            Child = stack,
        };
        HistoryPanel.Children.Add(bubble);
        HistoryScroll.ScrollToEnd();

        async Task Respond(string decision, bool remember, string statusText)
        {
            CollapsePermissionBubble(bubble, statusText, p.ToolName);
            try { if (_vm.Host is not null) await _vm.Host.RespondPermissionAsync(p.RequestId, decision, remember); }
            catch { }
        }

        deny.Click += async (_, _) =>
        {
            _recentlyDeniedKeys.Add(p.ToolName + "|" + p.InputJson);
            await Respond("deny", remember: false, "× Denied");
        };
        once.Click += async (_, _) => await Respond("allow", remember: false, "✓ Allowed once");
        always.Click += async (_, _) => await Respond("allow", remember: true, "✓ Allowed always");
    }

    /// <summary>
    /// Replace a resolved permission bubble with a compact, background-less
    /// status line at the same position in the chat history. The
    /// preserved-action context (decision + tool name) sits inline as a
    /// quiet trace of what happened.
    /// </summary>
    void CollapsePermissionBubble(Border bubble, string statusText, string toolName)
    {
        var idx = HistoryPanel.Children.IndexOf(bubble);
        if (idx < 0) return;
        var line = new TextBlock
        {
            Text = statusText + " · " + toolName,
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        HistoryPanel.Children.RemoveAt(idx);
        HistoryPanel.Children.Insert(idx, line);
    }

    static readonly Brush ToolFailedFg = Frozen(Color.FromRgb(240, 80, 80));

    void OnVmToolCallObserved(ToolCallEvent e)
    {
        var key = !string.IsNullOrEmpty(e.CallId) ? e.CallId : e.ToolName + "|" + e.InputJson;
        var summary = SummarizeToolCall(e.ToolName, e.InputJson);
        var isRequested = string.Equals(e.Phase, "requested", StringComparison.OrdinalIgnoreCase);

        // Failed-because-user-denied is a quiet "skipped" outcome, not a red error.
        var phase = e.Phase;
        if (string.Equals(e.Phase, "failed", StringComparison.OrdinalIgnoreCase)
            && _recentlyDeniedKeys.Remove(e.ToolName + "|" + e.InputJson))
            phase = "denied";

        if (isRequested) SplitStreamingBubble();

        if (!isRequested && _toolCallLines.TryGetValue(key, out var existing))
        {
            StyleToolCallLine(existing, summary, phase);
            _toolCallLines.Remove(key);
            return;
        }

        var line = MakeToolCallLine(summary, phase);
        if (isRequested) _toolCallLines[key] = line;
        HistoryPanel.Children.Add(line);
        HistoryScroll.ScrollToEnd();
    }

    TextBlock MakeToolCallLine(string summary, string phase)
    {
        var line = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        StyleToolCallLine(line, summary, phase);
        return line;
    }

    void StyleToolCallLine(TextBlock line, string summary, string phase)
    {
        if (string.Equals(phase, "requested", StringComparison.OrdinalIgnoreCase))
        {
            line.Text = "🔧 " + summary + " …";
            line.FontStyle = FontStyles.Italic;
        }
        else if (string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase))
        {
            line.Text = "⚠ " + summary;
            line.FontStyle = FontStyles.Normal;
            line.Foreground = ToolFailedFg;
            line.Opacity = 1.0; // full saturation — failures shouldn't fade with the rest
        }
        else if (string.Equals(phase, "denied", StringComparison.OrdinalIgnoreCase))
        {
            // User-denied — same neutral gray as completed, just a different glyph.
            line.Text = "× " + summary;
            line.FontStyle = FontStyles.Normal;
        }
        else
        {
            line.Text = "✓ " + summary;
            line.FontStyle = FontStyles.Normal;
        }
    }

    /// <summary>
    /// Format a tool call as a one-line human label. Pulls the most
    /// useful argument out of the JSON for the well-known tools (file
    /// path, command, search pattern); falls back to the bare tool name
    /// for anything we don't recognise — including MCP tools, where the
    /// server.tool naming is already informative.
    /// </summary>
    static string SummarizeToolCall(string toolName, string inputJson)
    {
        if (string.IsNullOrEmpty(inputJson)) return toolName;
        string? Arg(System.Text.Json.JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            string? detail = toolName switch
            {
                "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit"
                    => Arg(root, "file_path") ?? Arg(root, "filePath") ?? Arg(root, "path"),
                "Bash" or "BashOutput" or "PowerShell"
                    => Arg(root, "command"),
                "Glob"
                    => Arg(root, "pattern"),
                "Grep"
                    => Arg(root, "pattern"),
                "WebFetch" or "WebSearch"
                    => Arg(root, "url") ?? Arg(root, "query"),
                _ => null,
            };
            if (string.IsNullOrEmpty(detail)) return toolName;
            const int max = 80;
            if (detail!.Length > max) detail = detail.Substring(0, max - 1) + "…";
            return toolName + " " + detail;
        }
        catch { return toolName; }
    }

    Button PermissionButton(string label, string styleKey) => new()
    {
        Content = label,
        Style = (Style)FindResource(styleKey),
    };

    static string PrettyJson(string json)
    {
        try { using var doc = System.Text.Json.JsonDocument.Parse(json); return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        catch { return json; }
    }

    // ---- Streaming bubble UI helpers -------------------------------------

    StackPanel EnsureStreamingBubble()
    {
        if (_streamingStack is not null) return _streamingStack;
        var stack = new StackPanel();
        _streamingLabelTb = new TextBlock
        {
            Text = _vm.InitialModelLabel,
            FontWeight = FontWeights.Bold,
            Foreground = Accent,
        };
        stack.Children.Add(_streamingLabelTb);
        HistoryPanel.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Background = AssistantBubbleBg,
            Child = stack,
        });
        _streamingStack = stack;
        return stack;
    }

    void ShowThinkingDots()
    {
        if (_thinkingBubble is not null) return;
        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        dots.Children.Add(ThinkingDot(0.0));
        dots.Children.Add(ThinkingDot(0.2));
        dots.Children.Add(ThinkingDot(0.4));
        _thinkingBubble = new Border
        {
            Background = AssistantBubbleBg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = dots,
        };
        HistoryPanel.Children.Add(_thinkingBubble);
        HistoryScroll.ScrollToEnd();
    }

    void HideThinkingDots()
    {
        if (_thinkingBubble is null) return;
        HistoryPanel.Children.Remove(_thinkingBubble);
        _thinkingBubble = null;
    }

    // Re-show the dots if no chunk lands within ~600ms — covers the "model
    // streamed an answer, paused to do tool work, then resumed" gap.
    void RestartIdleDotsTimer()
    {
        if (_idleDotsTimer is null)
        {
            _idleDotsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _idleDotsTimer.Tick += (_, _) =>
            {
                _idleDotsTimer?.Stop();
                if (_vm.StreamingSessionId is not null) ShowThinkingDots();
            };
        }
        _idleDotsTimer.Stop();
        _idleDotsTimer.Start();
    }

    void StopIdleDotsTimer() => _idleDotsTimer?.Stop();

    static Ellipse ThinkingDot(double delaySec)
    {
        var e = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = Frozen(Color.FromArgb(180, 128, 128, 128)),
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.3,
        };
        var anim = new DoubleAnimation
        {
            From = 0.3, To = 1.0,
            Duration = new Duration(TimeSpan.FromSeconds(0.6)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(delaySec),
        };
        e.BeginAnimation(UIElement.OpacityProperty, anim);
        return e;
    }

    // ---- Bubble building helpers -----------------------------------------

    static TextBlock ThinkingBlock()
    {
        var t = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Opacity = 0.75,
            Margin = new Thickness(12, 4, 0, 4),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return t;
    }

    static Expander ThinkingExpander(TextBlock body)
    {
        var e = new Expander
        {
            Header = "💭 Thinking",
            IsExpanded = false,
            Margin = new Thickness(0, 4, 0, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Content = body,
        };
        e.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return e;
    }

    static TextBlock FooterBlock()
    {
        var t = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(0, 6, 0, 0),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return t;
    }

    static void AppendUserTextParts(StackPanel stack, string text)
    {
        var parts = text.Split(new[] { "```" }, StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim('\r', '\n');
            if (string.IsNullOrEmpty(part)) continue;
            if (i % 2 == 1)
            {
                var nl = part.IndexOf('\n');
                if (nl >= 0)
                {
                    var first = part.Substring(0, nl).Trim();
                    if (first.Length > 0 && first.Length < 20 && !first.Contains(' '))
                        part = part.Substring(nl + 1).Trim('\r', '\n');
                }
                var code = new TextBlock
                {
                    Text = part,
                    FontFamily = new FontFamily("Consolas, Lucida Sans Typewriter, Courier New"),
                    TextWrapping = TextWrapping.NoWrap,
                };
                code.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                stack.Children.Add(new Border
                {
                    Background = MarkdownRenderer.CodeBackground,
                    Padding = new Thickness(6),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 2, 0, 2),
                    Child = code,
                });
            }
            else
            {
                var plain = new TextBlock
                {
                    Text = part,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                plain.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                stack.Children.Add(plain);
            }
        }
    }

    // Both UsagePayload (loaded from history) and UsageParams (live from
    // the host) carry the same three fields; one helper avoids drift.
    static string FormatUsage(UsagePayload u) => FormatUsage(u.InputTokens, u.OutputTokens, u.CostUsd);
    static string FormatUsage(UsageParams u)  => FormatUsage(u.InputTokens, u.OutputTokens, u.CostUsd);

    static string FormatUsage(int inputTokens, int outputTokens, double? costUsd)
    {
        var parts = new List<string>();
        if (inputTokens > 0) parts.Add($"in {inputTokens}");
        if (outputTokens > 0) parts.Add($"out {outputTokens}");
        if (costUsd is > 0) parts.Add($"${costUsd:0.####}");
        return string.Join(" • ", parts);
    }

    // ---- Reference chips -------------------------------------------------

    void OnReferencesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReferencesExpander.Visibility = _vm.References.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (int i = 0; i < e.NewItems.Count; i++)
                    ReferencesList.Children.Insert(e.NewStartingIndex + i,
                        CreateReferenceChip((ReferenceItem)e.NewItems[i]!, interactive: true));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (int i = 0; i < e.OldItems.Count; i++)
                    if (e.OldStartingIndex < ReferencesList.Children.Count)
                        ReferencesList.Children.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                ReferencesList.Children.Clear();
                break;
        }
    }

    FrameworkElement CreateReferenceChip(ReferenceItem item, bool interactive)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
        };
        border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarSelectedBrushKey);

        RebuildChipContent(content, item, interactive);
        if (interactive)
        {
            PropertyChangedEventHandler handler = (_, _) => RebuildChipContent(content, item, interactive);
            item.PropertyChanged += handler;
            border.Unloaded += (_, _) => item.PropertyChanged -= handler;
        }
        return border;
    }

    void RebuildChipContent(StackPanel content, ReferenceItem item, bool interactive)
    {
        content.Children.Clear();

        var filename = new TextBlock
        {
            Text = item.FilePath,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 250,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        filename.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        filename.MouseLeftButtonUp += (_, e) => { Editor.EditorSelectionService.Navigate(item.AbsolutePath, 0, 0); e.Handled = true; };
        content.Children.Add(filename);

        if (!item.IsWholeFile)
        {
            int count = item.Ranges.Count;
            for (int i = 0; i < count; i++)
            {
                var range = item.Ranges[i];
                var sep = i == 0 ? " :" : (i == count - 1 ? " & " : ", ");
                var sepText = new TextBlock
                {
                    Text = sep,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                sepText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                content.Children.Add(sepText);
                content.Children.Add(BuildRangeGroup(item, range, interactive));
            }
        }

        if (interactive)
        {
            var removeAll = BuildCloseGlyph(() => _vm.References.Remove(item), "Remove reference");
            removeAll.Margin = new Thickness(6, 0, 0, 0);
            content.Children.Add(removeAll);
        }
    }

    FrameworkElement BuildRangeGroup(ReferenceItem item, LineRange range, bool interactive)
    {
        var rangeText = new TextBlock
        {
            Text = range.Display.TrimStart(':'),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = RangeBlue,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rangeText.MouseLeftButtonUp += (_, e) => { Editor.EditorSelectionService.Navigate(item.AbsolutePath, range.Start, range.End); e.Handled = true; };
        if (!interactive) return rangeText;

        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        group.Children.Add(rangeText);

        var x = BuildCloseGlyph(() =>
        {
            item.Ranges.Remove(range);
            if (item.Ranges.Count == 0 && !item.IsWholeFile) _vm.References.Remove(item);
            else item.NotifyChanged();
        }, "Remove this range");
        x.Opacity = 0;
        group.Children.Add(x);
        group.MouseEnter += (_, _) => x.Opacity = 1;
        group.MouseLeave += (_, _) => x.Opacity = 0;
        return group;
    }

    static TextBlock BuildCloseGlyph(Action onClick, string tooltip)
    {
        var x = new TextBlock
        {
            Text = "×",
            FontSize = 12,
            Foreground = Brushes.Gray,
            Cursor = Cursors.Hand,
            Padding = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };
        x.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return x;
    }

    // ---- MCP popup + Settings dialog -------------------------------------

    async void McpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Host is null) return;
        if (McpPopup.Child is not McpToolMenu menu)
        {
            menu = new McpToolMenu(_vm.Host);
            McpPopup.Child = menu;
        }
        McpPopup.IsOpen = true;
        await menu.LoadAsync();
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Host is null) return;
        var win = new Settings.SettingsWindow(_vm.Host) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    // ---- External command bridge (SendSelection / AddFile) ---------------

    public void AppendReference(string displayPath, string absolutePath, int startLine, int endLine, string content)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _vm.MarkInteracted();
        _vm.AppendReference(displayPath, absolutePath, startLine, endLine, content);
    }

    // ---- Lifecycle -------------------------------------------------------

    public void DisposeHost() => _vm.DisposeHost(InputBox.Text, DraftSaveOnShutdownBudget);

    static readonly TimeSpan ChangesResolveOnShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>Called from <see cref="ChatRelayPackage.QueryClose"/>; returns false to veto VS shutdown.</summary>
    public bool ConfirmCloseWithPendingChanges()
    {
        var sessionId = _vm.CurrentSession?.Id;
        if (sessionId is null || _vm.Host is null) return true;

        // Proposals rows stick around after accept/deny; HasOpenChanges filters out the resolved ones.
        var fileCount = _vm.Proposals.Count(p => p.HasOpenChanges);
        if (fileCount == 0) return true;

        var msg = $"ChatRelay has {fileCount} pending file change{(fileCount == 1 ? "" : "s")} from the AI.\n\n" +
                  "Yes — Accept all and exit\nNo — Deny all and exit\nCancel — Stay in Visual Studio";
        var result = MessageBox.Show(
            Window.GetWindow(this) ?? Application.Current?.MainWindow,
            msg, "ChatRelay — pending changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return false;

#pragma warning disable VSTHRD002
        try
        {
            var task = result == MessageBoxResult.Yes
                ? _vm.Host.AcceptAllOpenChangesAsync(sessionId)
                : _vm.Host.DenyAllOpenChangesAsync(sessionId);
            task.Wait(ChangesResolveOnShutdownBudget);
        }
        catch { }
#pragma warning restore VSTHRD002
        return true;
    }

    void SetStatus(string text) => UsageStatusBar.Text = text;

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ---- Changes list rendering ------------------------------------------

    void OnProposalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (int i = 0; i < e.NewItems.Count; i++)
                    ChangesList.Children.Insert(e.NewStartingIndex + i,
                        BuildProposalRow((ChangeItem)e.NewItems[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (int i = 0; i < e.OldItems.Count; i++)
                    if (e.OldStartingIndex < ChangesList.Children.Count)
                        ChangesList.Children.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                ChangesList.Children.Clear();
                break;
        }
        ChangesContainer.Visibility = _vm.Proposals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnDenialsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (int i = 0; i < e.NewItems.Count; i++)
                    DenialsList.Children.Insert(e.NewStartingIndex + i,
                        BuildDenialRow((DenialItem)e.NewItems[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (int i = 0; i < e.OldItems.Count; i++)
                    if (e.OldStartingIndex < DenialsList.Children.Count)
                        DenialsList.Children.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                DenialsList.Children.Clear();
                break;
        }
        DenialsExpander.Visibility = _vm.Denials.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Auto-expand on first incoming change. The "expand" here is just the
    // fact that the section becomes visible when Proposals goes 0 → 1+;
    // OnProposalsChanged does that already, so this handler is reserved
    // for future bookkeeping (telemetry, focus stealing, etc.). Currently
    // a no-op but kept wired so the spec contract holds explicitly.
    void OnVmProposalsBecameNonEmpty()
    {
        ChangesContainer.Visibility = Visibility.Visible;
    }

    FrameworkElement BuildProposalRow(ChangeItem item)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
        border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarSelectedBrushKey);

        RebuildProposalContent(content, item);

        // Per-row INPC: re-render when LinesAdded / LinesRemoved / State
        // change in place (a follow-on edit to the same file fires those
        // setters from ApplySnapshot).
        PropertyChangedEventHandler handler = (_, _) => RebuildProposalContent(content, item);
        item.PropertyChanged += handler;
        border.Unloaded += (_, _) => item.PropertyChanged -= handler;
        return border;
    }

    void RebuildProposalContent(StackPanel content, ChangeItem item)
    {
        content.Children.Clear();

        // Both buttons hide once everything's accepted — nothing left to accept or undo on this file.
        var accept = new Button
        {
            Content = "✓",
            ToolTip = "Accept this change",
            Style = (Style)FindResource("ChangeRowButtonStyle"),
            Visibility = item.HasOpenChanges ? Visibility.Visible : Visibility.Collapsed,
        };
        accept.Click += async (_, _) => await _vm.AcceptChangeAsync(item);
        var undo = new Button
        {
            Content = "↶",
            ToolTip = "Undo this change",
            Style = (Style)FindResource("ChangeRowButtonStyle"),
            Visibility = item.HasOpenChanges ? Visibility.Visible : Visibility.Collapsed,
        };
        undo.Click += async (_, _) => await _vm.DenyChangeAsync(item);

        content.Children.Add(accept);
        content.Children.Add(undo);

        var path = new TextBlock
        {
            Text = item.FilePath,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 250,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
        };
        path.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        path.MouseLeftButtonUp += (_, e) =>
        {
            Editor.EditorSelectionService.Navigate(item.AbsolutePath, 0, 0);
            e.Handled = true;
        };
        content.Children.Add(path);

        // Open diff (bright) followed by accepted history (dimmed) so the row always shows totals.
        AddCount(content, item.LinesAdded, "+", Frozen(Color.FromRgb(0x2E, 0xCC, 0x71)), 1.0);
        AddCount(content, item.LinesRemoved, "−", Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)), 1.0);
        AddCount(content, item.AcceptedLinesAdded, "+", Frozen(Color.FromRgb(0x2E, 0xCC, 0x71)), 0.55);
        AddCount(content, item.AcceptedLinesRemoved, "−", Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)), 0.55);
    }

    // Drag up grows the list, drag down shrinks. Bounds keep at least one row visible and cap at the viewport.
    void ChangesResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        const double minH = 22;
        double maxH = Math.Max(minH, ActualHeight - 200);
        double next = ChangesScroll.MaxHeight - e.VerticalChange;
        ChangesScroll.MaxHeight = Math.Max(minH, Math.Min(maxH, next));
    }

    static void AddCount(StackPanel content, int n, string sign, Brush brush, double opacity)
    {
        if (n <= 0) return;
        content.Children.Add(new TextBlock
        {
            Text = sign + n,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = brush,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
    }

    FrameworkElement BuildDenialRow(DenialItem item)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.65,                      // dim — these are removed
            Child = content,
        };
        border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarSelectedBrushKey);

        RebuildDenialContent(content, item);
        PropertyChangedEventHandler handler = (_, _) => RebuildDenialContent(content, item);
        item.PropertyChanged += handler;
        border.Unloaded += (_, _) => item.PropertyChanged -= handler;
        return border;
    }

    void RebuildDenialContent(StackPanel content, DenialItem item)
    {
        content.Children.Clear();

        // Redo (↷) hidden once the file's drifted since the deny (CanRedo == false).
        var redo = new Button
        {
            Content = "↷",
            ToolTip = "Re-apply this change",
            Style = (Style)FindResource("ChangeRowButtonStyle"),
            Visibility = item.CanRedo ? Visibility.Visible : Visibility.Collapsed,
        };
        redo.Click += async (_, _) => await _vm.RedoDenialAsync(item);
        content.Children.Add(redo);

        var path = new TextBlock
        {
            Text = item.FilePath,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 250,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
        };
        path.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        path.MouseLeftButtonUp += (_, e) =>
        {
            Editor.EditorSelectionService.Navigate(item.AbsolutePath, 0, 0);
            e.Handled = true;
        };
        content.Children.Add(path);

        if (item.LinesAdded > 0)
        {
            var add = new TextBlock
            {
                Text = "+" + item.LinesAdded,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Frozen(Color.FromRgb(0x2E, 0xCC, 0x71)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            content.Children.Add(add);
        }
        if (item.LinesRemoved > 0)
        {
            var rem = new TextBlock
            {
                Text = "−" + item.LinesRemoved,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Frozen(Color.FromRgb(0xC0, 0x39, 0x2B)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(rem);
        }
    }

}
