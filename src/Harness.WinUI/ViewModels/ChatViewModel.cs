// Harness.WinUI — Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Core.Agent;
using Harness.Core.Artifacts;
using Harness.Core.Config;
using Harness.Core.History;
using Harness.Core.Tools;
using Harness.Core.Usage;
using Harness.MarkdownRendering;

namespace Harness.WinUI.ViewModels;

/// <summary>
/// Drives the single chat conversation shown in <see cref="Harness.WinUI.MainWindow"/>. Owns the
/// (possibly-null, see <see cref="_chatSession"/>) <see cref="ChatSession"/> and streams
/// assistant replies into the WebView2 shell via <see cref="IChatMessageSink"/>, buffering text
/// deltas on a fixed interval before each render flush (see Harness.MarkdownRendering.ChatShell's
/// doc comment for why: re-parsing Markdown and reflowing the DOM per token is wasted work and
/// causes visible flicker at typical LLM token rates).
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IToolApprover
{
    /// <summary>Minimum time between streamed-content DOM flushes, per the ChatShell streaming-render design.</summary>
    private const int FlushIntervalMs = 100;

    // Non-readonly: swapped by UpdateSession when the user saves new provider settings from the
    // Settings dialog, so the app picks up the new provider without a restart.
    private ChatSession? _chatSession;
    private readonly IChatMessageSink _messageSink;
    private readonly string? _startupErrorMessage;
    private readonly ConversationStore? _history;
    private int _messageCounter;

    // The conversation being shown; assigned when its first exchange is saved.
    private string? _conversationId;
    private string? _conversationTitle;

    // True after reopening a saved conversation until a reply succeeds (see SendAsync's error handling).
    private bool _resumedFromHistory;

    // Approval prompts are keyed by tool call id; the WebView answers via ResolveApproval.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalDecision>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, byte> _deniedCalls = new();
    private volatile string? _activeAssistantId;

    // Cancels the reply in progress (Stop button / Esc).
    private CancellationTokenSource? _turnCancellation;

    // Times the reply in progress; paused while approvals wait (several can wait at once with parallel tool calls).
    private Stopwatch? _turnStopwatch;
    private int _waitingApprovals;
    private readonly Lock _timerLock = new();

    private void PauseTurnTimer(bool pause)
    {
        lock (_timerLock)
        {
            _waitingApprovals += pause ? 1 : -1;
            if (pause && _waitingApprovals == 1)
                _turnStopwatch?.Stop();
            else if (!pause && _waitingApprovals == 0)
                _turnStopwatch?.Start();
        }
    }

    // Field-backed [ObservableProperty] form. Tried the partial-property form (recommended for
    // AOT/WinRT marshalling — MVVMTK0045 / WindowsAppSDK analyzer WUI3001) first, including with
    // an explicit LangVersion 13.0, but the CommunityToolkit.Mvvm 8.4.2 generator would not emit
    // an implementation part for a `public partial` auto-property in this
    // net9.0-windows10.0.19041.0 / UseWinUI project (CS9248). Since this app isn't NativeAOT-
    // published, the field-backed form is used instead; the resulting
    // MVVMTK0045 warning is expected and harmless here.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    /// <summary>"auto", "low", "medium" or "high" — sent with every turn (see <see cref="AppPreferences.ParseEffort"/>).</summary>
    public string ReasoningEffort { get; set; } = "auto";

    /// <summary>
    /// Context from MCP App views to add to the next message for the model (taken, so it's sent once);
    /// null when there is none. Called on the UI thread.
    /// </summary>
    public Func<string?>? AppContextProvider { get; set; }

    /// <summary>Default image quality, for the status line's time estimate (the tool reads its own copy).</summary>
    public string ImageQuality { get; set; } = "low";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpBadgeText))]
    private int _mcpToolCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpBadgeText))]
    private bool _mcpStarting;

    [ObservableProperty]
    private bool _mcpHasErrors;

    [ObservableProperty]
    private string _mcpStatusText = Strings.NoMcpServers;

    public string McpBadgeText => McpStarting ? "…" : McpToolCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="history">Where conversations are saved; null runs without history (e.g. the database couldn't be opened).</param>
    public ChatViewModel(ChatSession? chatSession, IChatMessageSink messageSink, ConversationStore? history, string? startupErrorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(messageSink);

        _chatSession = chatSession;
        _messageSink = messageSink;
        _history = history;
        _startupErrorMessage = startupErrorMessage;
    }

    /// <summary>Recent conversations for the History flyout (see <see cref="RefreshHistoryAsync"/>).</summary>
    public ObservableCollection<ConversationItem> History { get; } = [];

    /// <summary>Call once the WebView2 shell has finished loading. Surfaces a startup config error, if any, as a system bubble.</summary>
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_startupErrorMessage))
            return;

        await PostSystemMessageAsync(_startupErrorMessage).ConfigureAwait(true);
    }

    public bool HasSession => _chatSession is not null;

    /// <summary>
    /// Swaps in a newly-built <see cref="ChatSession"/> — called after the user saves new provider
    /// settings from the Settings dialog, so the app becomes usable immediately without a restart.
    /// </summary>
    public void UpdateSession(ChatSession newSession)
    {
        ArgumentNullException.ThrowIfNull(newSession);
        _chatSession = newSession;
    }

    /// <summary>Proactively posts the "not connected" system bubble, e.g. after the user cancels initial setup.</summary>
    public Task NotifyNotConfiguredAsync() => PostSystemMessageAsync(Strings.NotConfigured);

    /// <summary>Posts a system bubble explaining why a provider-settings change couldn't be applied.</summary>
    public Task NotifyProviderErrorAsync(string message) => PostSystemMessageAsync(message);

    /// <summary>The badge counts every tool the agent can call; the tooltip breaks it down.</summary>
    public void UpdateToolStatus(IReadOnlyList<McpServerStatus> servers, int builtInToolCount, string? sandboxRoot)
    {
        McpToolCount = builtInToolCount + servers.Where(s => s.State == McpServerState.Connected).Sum(s => s.ToolCount);
        McpStarting = servers.Any(s => s.State == McpServerState.Starting);
        McpHasErrors = servers.Any(s => s.State == McpServerState.Failed);
        var builtIn = Strings.BuiltInTools(builtInToolCount) + "\n" +
            (sandboxRoot is null ? Strings.FileToolsOff : Strings.FileToolsIn(sandboxRoot));
        McpStatusText = builtIn + "\n\n" + (servers.Count == 0
            ? Strings.NoMcpServers
            : string.Join("\n", servers.Select(s => s.State switch
            {
                McpServerState.Connected => Strings.ServerTools(s.Name, s.ToolCount),
                McpServerState.Starting => Strings.ServerStarting(s.Name),
                McpServerState.Disabled => Strings.ServerDisabled(s.Name),
                _ => Strings.ServerFailed(s.Name, s.Error),
            })));
    }

    public async Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var messageId = _activeAssistantId;
        if (messageId is null)
            return ToolApprovalDecision.Deny;

        var pending = new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[request.CallId] = pending;
        try
        {
            await _messageSink.UpsertMessageStepAsync(messageId, ChatMarkup.ToolStepId(request.CallId), ChatMarkup.ApprovalStepHtml(request), StepState.Waiting);
            // Time spent waiting for the user isn't the agent's: freeze the live counter and the reply's timer.
            await _messageSink.SetMessageStatusAsync(messageId, Strings.WaitingForApproval, paused: true);
            PauseTurnTimer(true);
            ApprovalRequested?.Invoke(this, request.ToolName);

            ToolApprovalDecision decision;
            try
            {
                using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)))
                    decision = await pending.Task;
            }
            finally
            {
                PauseTurnTimer(false);
            }

            var call = new ToolCallStarted(request.CallId, request.ToolName, request.Arguments.ToDictionary());
            if (decision == ToolApprovalDecision.Deny)
            {
                _deniedCalls[request.CallId] = 0;
                await _messageSink.UpsertMessageStepAsync(messageId, ChatMarkup.ToolStepId(request.CallId), ChatMarkup.ToolStepHtml(call, note: Strings.Declined), StepState.Error);
                await _messageSink.SetMessageStatusAsync(messageId, ThinkingStatus);
            }
            else
            {
                await _messageSink.UpsertMessageStepAsync(messageId, ChatMarkup.ToolStepId(request.CallId), ChatMarkup.ToolStepHtml(call), StepState.Running);
                await _messageSink.SetMessageStatusAsync(messageId, Strings.RunningTool(request.ToolName));
            }

            return decision;
        }
        finally
        {
            _pendingApprovals.TryRemove(request.CallId, out _);
        }
    }

    /// <summary>Called when the user clicks an approval button in the chat. Unknown call ids are ignored.</summary>
    public void ResolveApproval(string callId, string decision)
    {
        if (!_pendingApprovals.TryGetValue(callId, out var pending))
            return;

        pending.TrySetResult(decision switch
        {
            "allow" => ToolApprovalDecision.AllowOnce,
            "always" => ToolApprovalDecision.AlwaysAllow,
            _ => ToolApprovalDecision.Deny,
        });
    }

    private Task PostSystemMessageAsync(string message)
    {
        var html = ChatMarkdownRenderer.RenderBody(message);
        return _messageSink.AppendMessageAsync(NextMessageId(), "system", html);
    }

    private bool CanSend() => !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0);

    public const int MaxAttachments = 10;

    /// <summary>Images and files pasted or dropped into the composer, sent with the next message.</summary>
    public ObservableCollection<Attachment> Attachments { get; } = [];

    /// <summary>Adds an attachment; false when there are already <see cref="MaxAttachments"/>.</summary>
    public bool AddAttachment(Attachment attachment)
    {
        if (Attachments.Count >= MaxAttachments)
            return false;
        Attachments.Add(attachment);
        SendCommand.NotifyCanExecuteChanged();
        return true;
    }

    public void RemoveAttachment(Attachment attachment)
    {
        Attachments.Remove(attachment);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Starts over: the model's context grows with every turn (slower, costlier replies, eventually the
    /// context limit), so this is the way to keep long-running use light.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartNewChat))]
    private async Task NewChatAsync()
    {
        _chatSession?.Reset();
        _conversationId = null;
        _conversationTitle = null;
        _resumedFromHistory = false;
        ConversationCostText = string.Empty;
        await _messageSink.ClearConversationAsync().ConfigureAwait(true);
    }

    private bool CanStartNewChat() => !IsBusy;

    #region Usage and cost

    // Null if usage.db can't be opened: chat works, costs just aren't tracked.
    private readonly UsageLedger? _ledger = OpenLedger();

    private static UsageLedger? OpenLedger()
    {
        try
        {
            return new UsageLedger();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Usage ledger unavailable: {ex}");
            return null;
        }
    }

    /// <summary>This conversation's total so far (cost, or tokens when no price is set), for the usage button.</summary>
    [ObservableProperty]
    private string _conversationCostText = string.Empty;

    /// <summary>Records one reply's usage and returns its cost text for the reply's footer (null when the model has no price).</summary>
    private async Task<(string? Cost, string? Tooltip)> RecordUsageAsync(string? model, UsageReported? usage)
    {
        if (usage is not { InputTokens: { } input, OutputTokens: { } output })
            return (null, null);

        var preferences = AppPreferences.Load();
        var price = preferences.PriceFor(model);
        var cached = usage.CachedInputTokens ?? 0;
        var cost = price?.Cost(input, cached, output);
        _conversationId ??= Guid.NewGuid().ToString("N"); // usage is tracked even when history isn't saved
        var entry = new UsageEntry(DateTimeOffset.Now, _conversationId, model, input, cached, output, cost, preferences.Currency);

        if (_ledger is { } ledger)
        {
            try
            {
                await Task.Run(() => ledger.Record(entry)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                Debug.WriteLine($"Couldn't record usage: {ex}");
            }
        }
        await RefreshConversationCostAsync().ConfigureAwait(true);

        return cost is { } amount && price is not null
            ? (ChatMarkup.FormatCost(amount, preferences.Currency), Strings.CostTooltip(
                ChatMarkup.FormatCost(amount, preferences.Currency), price.Input, price.CachedInput, price.Output, preferences.Currency))
            : (null, model is null ? null : Strings.NoPriceTooltip(model));
    }

    private async Task RefreshConversationCostAsync()
    {
        if (_ledger is not { } ledger || _conversationId is not { } id)
        {
            ConversationCostText = string.Empty;
            return;
        }
        var totals = await Task.Run(() => ledger.Totals(conversationId: id)).ConfigureAwait(true);
        ConversationCostText = ChatMarkup.FormatTotalsShort(totals);
    }

    /// <summary>Totals for the usage flyout: this conversation, today, this month, all time.</summary>
    public async Task<IReadOnlyList<(string Label, UsageTotals Totals)>> UsageReportAsync()
    {
        if (_ledger is not { } ledger)
            return [];
        var id = _conversationId;
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        return await Task.Run(() => (IReadOnlyList<(string, UsageTotals)>)
        [
            (Strings.UsageThisConversation, id is null ? UsageTotals.Empty : ledger.Totals(conversationId: id)),
            (Strings.UsageToday, ledger.Totals(since: today)),
            (Strings.UsageThisMonth, ledger.Totals(since: month)),
            (Strings.UsageAllTime, ledger.Totals()),
        ]).ConfigureAwait(true);
    }

    #endregion

    /// <summary>Stops the reply in progress; what has streamed so far stays visible.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _turnCancellation?.Cancel();

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var userMessage = InputText.Trim();
        var attachments = Attachments.ToList();
        if (userMessage.Length == 0 && attachments.Count == 0)
            return;

        InputText = string.Empty;
        Attachments.Clear();
        await SendMessageAsync(userMessage, attachments).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends <paramref name="text"/> as the user's next message on behalf of an MCP App view (ui/message),
    /// leaving whatever the user is typing alone. False while a reply is in progress.
    /// </summary>
    public bool TrySendFromApp(string text)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(text))
            return false;
        _ = SendMessageAsync(text.Trim());
        return true;
    }

    private async Task SendMessageAsync(string userMessage, IReadOnlyList<Attachment>? attachments = null)
    {
        attachments ??= [];
        var userHtml = ChatMarkdownRenderer.RenderBody(userMessage) + ChatMarkup.AttachmentsHtml(attachments);
        await _messageSink.AppendMessageAsync(NextMessageId(), "user", userHtml).ConfigureAwait(true);

        // Captured once so a settings change mid-reply doesn't swap sessions under this turn.
        var session = _chatSession;
        if (session is null)
        {
            await PostSystemMessageAsync(Strings.NotConfigured).ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        var id = NextMessageId();
        var stopwatch = Stopwatch.StartNew();
        lock (_timerLock)
        {
            _turnStopwatch = stopwatch;
            _waitingApprovals = 0;
        }
        TimeSpan? firstTextAt = null;
        UsageReported? usage = null;
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new Dictionary<string, ToolCallStarted>();
        var steps = new Dictionary<string, StoredStep>(); // insertion order = display order
        string? errorText = null;
        var stopped = false;
        (bool Taken, string? State) stateBeforeTurn = default;
        using var cancellation = new CancellationTokenSource();
        _turnCancellation = cancellation;
        var lastFlushUtc = DateTime.MinValue;
        var lastReasoningFlushUtc = DateTime.MinValue;
        var statusVisible = false;

        string? currentStatus = null;
        var presentedArtifacts = new Dictionary<string, (int Length, bool IsComplete)>();

        async Task SetStatusAsync(string? status)
        {
            statusVisible = !string.IsNullOrEmpty(status);
            currentStatus = status;
            await _messageSink.SetMessageStatusAsync(id, status).ConfigureAwait(true);
        }

        // Renders the reply so far: text as Markdown, each <artifact> as a card, with artifact content
        // streamed to the side panel. The status line stays up while an artifact is being written
        // (its content isn't shown in the bubble), and hides once plain text is streaming.
        async Task FlushReplyAsync(bool isFinal)
        {
            var segments = ArtifactParser.Parse(text.ToString(), streamIsComplete: isFinal);
            ArtifactSegment? writing = null;
            foreach (var artifact in segments.OfType<ArtifactSegment>())
            {
                if (!artifact.IsComplete)
                    writing = artifact;

                var changed = !presentedArtifacts.TryGetValue(artifact.Id, out var presented) ||
                    presented.IsComplete != artifact.IsComplete ||
                    (!artifact.IsComplete && presented.Length != artifact.Content.Length);
                if (changed)
                {
                    presentedArtifacts[artifact.Id] = (artifact.Content.Length, artifact.IsComplete);
                    await _messageSink.PresentArtifactAsync(artifact).ConfigureAwait(true);
                }
            }

            await _messageSink.UpdateMessageContentAsync(id, ChatMarkup.ReplyHtml(segments), isFinal).ConfigureAwait(true);

            var status = writing is null ? null : Strings.WritingArtifact(writing.Title);
            if (status is not null ? status != currentStatus : statusVisible)
                await SetStatusAsync(status).ConfigureAwait(true);
        }

        try
        {
            await _messageSink.AppendMessageAsync(id, "assistant", string.Empty).ConfigureAwait(true);
            _activeAssistantId = id;
            await SetStatusAsync(ThinkingStatus).ConfigureAwait(true);

            // Stopping mid-turn can leave the agent session half-updated (e.g. a tool call without its
            // result), which would break the next turn; a stopped turn rolls back to this snapshot.
            stateBeforeTurn = (true, await session.SaveStateAsync(cancellation.Token).ConfigureAwait(true));

            // MCP App views may have told us what the user did in them (ui/update-model-context); the model
            // gets that with this message, the transcript shows only what the user typed.
            var forModel = AppContextProvider?.Invoke() is { } appContext ? appContext + "\n\n" + userMessage : userMessage;

            await foreach (var evt in session.SendAsync(forModel, AppPreferences.ParseEffort(ReasoningEffort), cancellation.Token, attachments))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        firstTextAt ??= stopwatch.Elapsed;
                        text.Append(delta.Text);

                        var now = DateTime.UtcNow;
                        if ((now - lastFlushUtc).TotalMilliseconds >= FlushIntervalMs)
                        {
                            lastFlushUtc = now;
                            await FlushReplyAsync(isFinal: false).ConfigureAwait(true);
                        }
                        break;

                    case ReasoningDelta r:
                        // Summaries stream token by token: throttle like reply text (the full text is sent at the end).
                        reasoning.Append(r.Text);
                        if ((DateTime.UtcNow - lastReasoningFlushUtc).TotalMilliseconds >= FlushIntervalMs)
                        {
                            lastReasoningFlushUtc = DateTime.UtcNow;
                            await _messageSink.UpsertMessageStepAsync(
                                id, ReasoningStepId, ChatMarkup.ReasoningStepHtml(reasoning.ToString()), StepState.Running).ConfigureAwait(true);
                        }
                        break;

                    case ToolCallStarted call:
                        tools[call.CallId] = call;
                        steps[call.CallId] = new StoredStep(call.CallId, call.Name, ArgumentsJson(call.Arguments), "running");
                        await _messageSink.UpsertMessageStepAsync(
                            id, ChatMarkup.ToolStepId(call.CallId), ChatMarkup.ToolStepHtml(call), StepState.Running).ConfigureAwait(true);
                        await SetStatusAsync(call.Name == ImageGenerationTool.Name ? ChatMarkup.ImageStatus(call, ImageQuality) : Strings.RunningTool(call.Name)).ConfigureAwait(true);
                        break;

                    case ToolCallCompleted done when tools.TryGetValue(done.CallId, out var call):
                        // A declined call already shows as declined; its "result" is our refusal text.
                        if (_deniedCalls.TryRemove(done.CallId, out _))
                        {
                            steps[done.CallId] = steps[done.CallId] with { State = "error", Note = Strings.Declined };
                        }
                        else
                        {
                            var result = done.Exception is null ? done.Result : null;
                            steps[done.CallId] = steps[done.CallId] with
                            {
                                State = done.Exception is null ? "done" : "error",
                                Result = ChatMarkup.ResultJson(result),
                                Note = done.Exception?.Message,
                            };
                            await _messageSink.UpsertMessageStepAsync(
                                id, ChatMarkup.ToolStepId(done.CallId),
                                ChatMarkup.ToolStepHtml(call, note: done.Exception?.Message, result: result),
                                done.Exception is null ? StepState.Done : StepState.Error).ConfigureAwait(true);
                            if (done.Exception is null)
                                await _messageSink.PresentToolAppAsync(id, call, done.Result).ConfigureAwait(true);
                        }
                        await SetStatusAsync(ThinkingStatus).ConfigureAwait(true);
                        break;

                    case UsageReported u:
                        usage = usage?.Add(u) ?? u;
                        break;
                }
            }

            if (reasoning.Length > 0)
            {
                await _messageSink.UpsertMessageStepAsync(
                    id, ReasoningStepId, ChatMarkup.ReasoningStepHtml(reasoning.ToString()), StepState.Done).ConfigureAwait(true);
            }

            await FlushReplyAsync(isFinal: true).ConfigureAwait(true);
            _resumedFromHistory = false;
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            // Cancellation surfaces as OperationCanceledException or as a provider/HTTP exception wrapping it.
            stopped = true;
            foreach (var step in steps.Values.Where(s => s.State == "running").ToList())
            {
                steps[step.Id] = step with { State = "error", Note = Strings.Stopped };
                if (tools.TryGetValue(step.Id, out var call))
                {
                    await _messageSink.UpsertMessageStepAsync(
                        id, ChatMarkup.ToolStepId(step.Id), ChatMarkup.ToolStepHtml(call, note: Strings.Stopped), StepState.Error).ConfigureAwait(true);
                }
            }

            if (reasoning.Length > 0)
            {
                await _messageSink.UpsertMessageStepAsync(
                    id, ReasoningStepId, ChatMarkup.ReasoningStepHtml(reasoning.ToString()), StepState.Done).ConfigureAwait(true);
            }

            await FlushReplyAsync(isFinal: true).ConfigureAwait(true);

            try
            {
                if (stateBeforeTurn.Taken)
                    await session.RestoreStateAsync(stateBeforeTurn.State).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                session.Reset(); // can't restore: start fresh rather than keep a broken session
            }
        }
        catch (Exception ex)
        {
            errorText = Strings.ReplyError(ex.Message);
            if (_resumedFromHistory)
            {
                // The provider may no longer have the saved conversation (server-side history expires).
                session.Reset();
                _resumedFromHistory = false;
                errorText += "\n\n" + Strings.ResumedContextLost;
            }

            await _messageSink.UpdateMessageContentAsync(id, ChatMarkdownRenderer.RenderBody(errorText), isFinal: true).ConfigureAwait(true);
        }
        finally
        {
            lock (_timerLock)
            {
                stopwatch.Stop();
                _turnStopwatch = null;
            }
            _activeAssistantId = null;
            _turnCancellation = null;
            await _messageSink.SetMessageStatusAsync(id, null).ConfigureAwait(true);
            var (cost, costTooltip) = await RecordUsageAsync(session.ConfiguredModelId, usage).ConfigureAwait(true);
            var (meta, tooltip) = ChatMarkup.FormatMeta(session.ConfiguredModelId, usage, firstTextAt, stopwatch.Elapsed, cost, costTooltip);
            if (stopped)
            {
                meta = string.IsNullOrEmpty(meta) ? Strings.Stopped : $"{Strings.Stopped} · {meta}";
                tooltip = Strings.StoppedTooltip + (string.IsNullOrEmpty(tooltip) ? string.Empty : "\n\n" + tooltip);
            }
            await _messageSink.SetMessageMetaAsync(id, meta, tooltip).ConfigureAwait(true);

            var reply = new StoredMessage(
                "assistant", errorText ?? text.ToString(),
                reasoning.Length > 0 ? reasoning.ToString() : null,
                steps.Count > 0 ? [.. steps.Values] : null,
                meta, tooltip);
            await SaveTurnAsync(session, new StoredMessage("user", ChatMarkup.WithAttachmentNames(userMessage, attachments)), reply).ConfigureAwait(true);
            IsBusy = false;

            // A stop is the user's own doing; anything else may be worth a notification.
            if (!stopped)
                TurnFinished?.Invoke(this, new TurnFinishedEventArgs(errorText is null, errorText ?? text.ToString()));
        }
    }

    /// <summary>Raised when a reply finishes or fails (not when the user stopped it).</summary>
    public event EventHandler<TurnFinishedEventArgs>? TurnFinished;

    /// <summary>Raised (possibly off the UI thread) when a tool call waits for the user's approval; the argument is the tool name.</summary>
    public event EventHandler<string>? ApprovalRequested;

    #region History

    /// <summary>Saves one exchange plus the agent's session state. Best effort: history must never break chatting.</summary>
    private async Task SaveTurnAsync(ChatSession session, StoredMessage user, StoredMessage reply)
    {
        if (_history is null)
            return;

        try
        {
            _conversationId ??= Guid.NewGuid().ToString("N");
            _conversationTitle ??= TitleFrom(user.Text);
            var (conversationId, title) = (_conversationId, _conversationTitle);
            var state = await session.SaveStateAsync().ConfigureAwait(true);
            await Task.Run(() => _history.Append(conversationId, title, [user, reply], state)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Couldn't save conversation history: {ex}");
        }
    }

    /// <summary>Reloads the recent-conversations list shown in the History flyout.</summary>
    public async Task RefreshHistoryAsync()
    {
        if (_history is null)
            return;

        var conversations = await Task.Run(() => _history.List(50)).ConfigureAwait(true);
        History.Clear();
        foreach (var conversation in conversations)
            History.Add(new ConversationItem(conversation.Id, conversation.Title, Strings.RelativeTime(conversation.UpdatedAt), conversation.Id == _conversationId));
    }

    /// <summary>Replays a saved conversation into the transcript and restores the agent session so it can continue.</summary>
    public async Task OpenConversationAsync(string conversationId)
    {
        if (_history is null || IsBusy)
            return;

        var conversation = await Task.Run(() => _history.Load(conversationId)).ConfigureAwait(true);
        if (conversation is null)
            return;

        IsBusy = true;
        try
        {
            await _messageSink.ClearConversationAsync().ConfigureAwait(true);
            foreach (var message in conversation.Messages)
                await ReplayAsync(message).ConfigureAwait(true);

            _conversationId = conversation.Id;
            _conversationTitle = conversation.Title;
            _resumedFromHistory = false;
            await RefreshConversationCostAsync().ConfigureAwait(true);
            if (_chatSession is not null)
            {
                try
                {
                    await _chatSession.RestoreStateAsync(conversation.SessionState).ConfigureAwait(true);
                    _resumedFromHistory = conversation.SessionState is not null;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
                {
                    _chatSession.Reset();
                    await PostSystemMessageAsync(Strings.ResumedContextLost).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteConversationAsync(string conversationId)
    {
        if (_history is null)
            return;

        await Task.Run(() => _history.Delete(conversationId)).ConfigureAwait(true);
        if (conversationId == _conversationId)
            await NewChatAsync().ConfigureAwait(true);
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private async Task ReplayAsync(StoredMessage message)
    {
        var id = NextMessageId();
        if (message.Role != "assistant")
        {
            await _messageSink.AppendMessageAsync(id, message.Role, ChatMarkdownRenderer.RenderBody(message.Text)).ConfigureAwait(true);
            return;
        }

        await _messageSink.AppendMessageAsync(id, "assistant", string.Empty).ConfigureAwait(true);
        if (message.Reasoning is { } reasoning)
            await _messageSink.UpsertMessageStepAsync(id, ReasoningStepId, ChatMarkup.ReasoningStepHtml(reasoning), StepState.Done).ConfigureAwait(true);

        foreach (var step in message.Steps ?? [])
        {
            var call = new ToolCallStarted(step.Id, step.Name, ArgumentsFromJson(step.Arguments));
            object? result = step.Result is null ? null : JsonDocument.Parse(step.Result).RootElement.Clone();
            // Anything that was still running when the turn ended didn't finish.
            var state = step.State == "done" ? StepState.Done : StepState.Error;
            await _messageSink.UpsertMessageStepAsync(id, ChatMarkup.ToolStepId(step.Id), ChatMarkup.ToolStepHtml(call, step.Note, result), state).ConfigureAwait(true);
        }

        var segments = ArtifactParser.Parse(message.Text, streamIsComplete: true);
        foreach (var artifact in segments.OfType<ArtifactSegment>())
            await _messageSink.RestoreArtifactAsync(artifact).ConfigureAwait(true);

        await _messageSink.UpdateMessageContentAsync(id, ChatMarkup.ReplyHtml(segments), isFinal: true).ConfigureAwait(true);
        if (message.Meta is { } meta)
            await _messageSink.SetMessageMetaAsync(id, meta, message.MetaTooltip).ConfigureAwait(true);
    }

    private static string TitleFrom(string message)
    {
        var firstLine = message.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
    }

    private static string? ArgumentsJson(IDictionary<string, object?>? arguments) =>
        arguments is { Count: > 0 } ? JsonSerializer.Serialize(arguments) : null;

    private static IDictionary<string, object?>? ArgumentsFromJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

    #endregion

    private static string ThinkingStatus => Strings.Thinking;
    private const string ReasoningStepId = "reasoning";


    // Note: Harness.MarkdownRendering.ChatShell's appendMessage() prepends its own "msg-" prefix
    // when setting the DOM element id, so the id minted here is the bare suffix.
    private string NextMessageId() => $"{++_messageCounter}-{Guid.NewGuid():N}";
}

/// <param name="Succeeded">False when the reply failed with an error.</param>
/// <param name="Text">The reply (or the error message).</param>
public sealed record TurnFinishedEventArgs(bool Succeeded, string Text);
