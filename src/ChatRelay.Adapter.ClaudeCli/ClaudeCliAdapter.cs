using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Adapter over the local <c>claude</c> CLI. Stateful: re-uses Anthropic's
    /// server-side session via <c>--resume</c>, so we never need to send
    /// conversation history. Model list is hand-curated against what the
    /// current CLI accepts on <c>--model</c> — the CLI doesn't expose an
    /// "enumerate models" command.
    /// </summary>
    public class ClaudeCliAdapter : AiAdapterBase
    {
        public const string AdapterId = "claude-cli";

        // How long to wait for `claude --version` before declaring the CLI
        // unavailable. The command is local and should answer in well under
        // a second — anything longer means the install is broken or PATH
        // resolution is hitting a network drive.
        private static readonly TimeSpan VersionProbeBudget = TimeSpan.FromSeconds(3);

        public override string Id => AdapterId;
        public override string DisplayName => "Claude CLI";

        public override AiCapabilities Capabilities { get; } = new AiCapabilities
        {
            StatefulSessions = true,
            PermissionModes = true,
            Streaming = false
        };

        private readonly ClaudeCliService _cli = new ClaudeCliService();

        // Maps tool_use id → (ToolName, InputJson) so we can replay the
        // metadata when a tool_result arrives. Cleared at the start of
        // every SendPromptAsync — the CLI's tool ids are unique per turn.
        private readonly Dictionary<string, (string Name, string Input)> _pendingToolCalls = new();

        public ClaudeCliAdapter()
        {
            _cli.MessageReceived += (s, e) =>
            {
                switch (e.Type)
                {
                    case ClaudeEventType.System:
                        var model = ModelNameFormatter.TryExtractFromSystemEvent(e.Content, null);
                        if (!string.IsNullOrEmpty(model))
                            RaiseMessage(new AiMessageEvent
                            {
                                Kind = AiEventKind.ModelInfo,
                                ModelDisplayName = model
                            });
                        // Session id is captured inside ClaudeCliService; surface
                        // it after each event so the UI can pick it up.
                        if (!string.IsNullOrEmpty(_cli.SessionId))
                            RaiseMessage(new AiMessageEvent
                            {
                                Kind = AiEventKind.SessionUpdate,
                                SessionId = _cli.SessionId
                            });
                        break;

                    case ClaudeEventType.AssistantMessage:
                        // Emit thinking first so the control can attach it to
                        // the bubble this text block produces. An assistant
                        // event can contain thinking, text, or both.
                        if (!string.IsNullOrEmpty(e.Thinking))
                            RaiseMessage(new AiMessageEvent
                            {
                                Kind = AiEventKind.ThinkingMessage,
                                Content = e.Thinking
                            });
                        if (!string.IsNullOrEmpty(e.Content))
                            RaiseMessage(new AiMessageEvent
                            {
                                Kind = AiEventKind.AssistantMessage,
                                Content = e.Content
                            });
                        // tool_use blocks fire a "Requested" observation so
                        // the change tracker can snapshot pre-write content.
                        // Cache id → (name, input) for the matching tool_result.
                        foreach (var use in e.ToolUses)
                        {
                            if (!string.IsNullOrEmpty(use.Id))
                                _pendingToolCalls[use.Id] = (use.Name, use.InputJson);
                            RaiseToolCall(use.Name, use.InputJson, ToolCallPhase.Requested, use.Id);
                        }
                        break;

                    case ClaudeEventType.User:
                        // tool_result blocks correlate by id. Replay the cached
                        // (name, input) so the tracker can resolve the file path
                        // without re-parsing JSON. is_error flips Completed → Failed.
                        foreach (var result in e.ToolResults)
                        {
                            if (string.IsNullOrEmpty(result.ToolUseId)) continue;
                            if (_pendingToolCalls.TryGetValue(result.ToolUseId, out var tup))
                            {
                                RaiseToolCall(tup.Name, tup.Input,
                                    result.IsError ? ToolCallPhase.Failed : ToolCallPhase.Completed,
                                    result.ToolUseId);
                                _pendingToolCalls.Remove(result.ToolUseId);
                            }
                        }
                        break;

                    case ClaudeEventType.Result when e.HasUsage:
                        // Emit the aggregated per-turn totals (includes tool-use
                        // hops). The result event arrives after all assistants
                        // so the UI already has a bubble to stamp onto.
                        RaiseMessage(new AiMessageEvent
                        {
                            Kind = AiEventKind.UsageUpdate,
                            Usage = new AiUsage
                            {
                                InputTokens = e.InputTokens,
                                OutputTokens = e.OutputTokens,
                                CacheReadTokens = e.CacheReadTokens,
                                CacheWriteTokens = e.CacheWriteTokens,
                                CostUsd = e.CostUsd
                            }
                        });
                        break;
                }
            };
            _cli.ErrorReceived += (s, err) =>
                RaiseError(err);
        }

        public override async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = new Process { StartInfo = psi })
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    proc.Start();
                    linked.CancelAfter(VersionProbeBudget);
                    try
                    {
                        await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Probe deadline expired — kill and report unavailable.
                        try { proc.Kill(); } catch { }
                        ExtensionLogger.Warn(AdapterId, "claude --version timed out");
                        return false;
                    }
                    var ok = proc.ExitCode == 0;
                    ExtensionLogger.Info(AdapterId, ok ? "CLI detected" : $"claude --version exit {proc.ExitCode}");
                    return ok;
                }
            }
            catch (Exception ex)
            {
                ExtensionLogger.Info(AdapterId, "CLI not available: " + ex.Message);
                return false;
            }
        }

        public override Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
        {
            // The CLI's --model flag accepts a short alias (opus/sonnet/haiku)
            // or a pinned id. We expose the aliases here; version labels are
            // informational — the CLI picks the concrete pinned model.
            IReadOnlyList<AiModel> list = new List<AiModel>
            {
                Make("",       "Default",  ""),
                Make("opus",   "Opus",     "latest"),
                Make("sonnet", "Sonnet",   "latest"),
                Make("haiku",  "Haiku",    "latest")
            };
            return Task.FromResult(list);
        }

        private AiModel Make(string id, string name, string version) => new AiModel
        {
            AdapterId = Id,
            AdapterDisplayName = DisplayName,
            Id = id,
            DisplayName = name,
            Version = version
        };

        public override async Task SendPromptAsync(AiRequest request, CancellationToken ct)
        {
            // Reset the per-turn tool-call cache. ids are unique per CLI
            // invocation — leftovers from a prior turn would alias.
            _pendingToolCalls.Clear();

            // Resume the server-side session if we have one; send just the
            // prompt (history lives on Anthropic's side).
            _cli.SessionId = request.SessionId;
            _cli.Model = string.IsNullOrEmpty(request.Model) ? null : request.Model;
            _cli.McpConfigPath = request.McpConfigPath;
            _cli.WorkingDirectory = request.WorkingDirectory;
            _cli.PermissionPromptTool = request.PermissionPromptTool;
            _cli.AllowedTools = request.AllowedTools;
            _cli.DisallowedTools = request.DisallowedTools;
            _cli.AdditionalDirectories = request.AdditionalDirectories;

            ExtensionLogger.Info(AdapterId,
                $"Send: model={_cli.Model ?? "<default>"} " +
                $"resume={!string.IsNullOrEmpty(_cli.SessionId)} " +
                $"mcp={!string.IsNullOrEmpty(_cli.McpConfigPath)} " +
                $"broker={!string.IsNullOrEmpty(_cli.PermissionPromptTool)} " +
                $"allow={_cli.AllowedTools?.Count ?? 0} deny={_cli.DisallowedTools?.Count ?? 0} " +
                $"promptLen={request.Prompt?.Length ?? 0}");

            try
            {
                await _cli.SendPromptAsync(request.Prompt ?? string.Empty, ct).ConfigureAwait(false);
            }
            finally
            {
                // Any tool_use without a matching tool_result by turn-end
                // failed (CLI died, turn cancelled, model abandoned the call).
                // Fire Failed so the UI can resolve the in-flight line.
                foreach (var kv in _pendingToolCalls)
                    RaiseToolCall(kv.Value.Name, kv.Value.Input, ToolCallPhase.Failed, kv.Key);
                _pendingToolCalls.Clear();

                // Emit one final session update so the caller picks up any id
                // assigned on this turn.
                if (!string.IsNullOrEmpty(_cli.SessionId))
                    RaiseMessage(new AiMessageEvent
                    {
                        Kind = AiEventKind.SessionUpdate,
                        SessionId = _cli.SessionId
                    });
            }
        }
    }
}
