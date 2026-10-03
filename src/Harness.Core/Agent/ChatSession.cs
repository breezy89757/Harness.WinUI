// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Harness.Core.Config;

namespace Harness.Core.Agent;

/// <summary>
/// Wraps a Microsoft.Agents.AI <see cref="AIAgent"/> for a single chat conversation.
/// Tools come from <c>toolsProvider</c> and are read fresh on every turn (passed as run options),
/// so tools that become available after the session was created — e.g. MCP servers still
/// starting in the background — are picked up from the next message on.
/// </summary>
public sealed class ChatSession
{
    private readonly AIAgent _agent;
    private readonly Func<IReadOnlyList<AITool>>? _toolsProvider;
    private readonly bool _reasoningSummaries;
    private AgentSession? _session;

    /// <param name="reasoningSummaries">Ask the model to stream reasoning summaries (Responses API only).</param>
    public ChatSession(
        IChatClient chatClient, HarnessOptions options, Func<IReadOnlyList<AITool>>? toolsProvider = null, bool reasoningSummaries = false)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(options);

        ConfiguredModelId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId;
        _toolsProvider = toolsProvider;
        _reasoningSummaries = reasoningSummaries;

        _agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = options.AgentName,
                ChatOptions = new ChatOptions { Instructions = options.Instructions },
            });
    }

    /// <summary>Forgets the conversation so far; the next message starts a fresh history.</summary>
    public void Reset() => _session = null;

    /// <summary>
    /// The agent session as JSON (message history, or just the server-side conversation id when the
    /// provider keeps the history), or null before the first message.
    /// </summary>
    public async Task<string?> SaveStateAsync(CancellationToken cancellationToken = default) =>
        _session is null ? null : (await _agent.SerializeSessionAsync(_session, cancellationToken: cancellationToken).ConfigureAwait(false)).GetRawText();

    /// <summary>Continues a conversation saved with <see cref="SaveStateAsync"/>; null starts fresh.</summary>
    public async Task RestoreStateAsync(string? state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(state))
        {
            _session = null;
            return;
        }

        using var document = JsonDocument.Parse(state);
        _session = await _agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The model id the chat client was built for, if the provider exposes it.</summary>
    public string? ConfiguredModelId { get; }

    /// <summary>
    /// Whether the provider can search the web itself (OpenAI / Azure OpenAI Responses API, the same
    /// condition as reasoning summaries). Chat Completions endpoints such as LiteLLM can't.
    /// </summary>
    public bool SupportsWebSearch => _reasoningSummaries;

    /// <summary>Offer the provider's hosted web search on the next turns (when <see cref="SupportsWebSearch"/>).</summary>
    public bool WebSearch { get; set; }

    public const string WebSearchToolName = "web_search";

    /// <summary>
    /// Sends a user message and streams the assistant's turn as <see cref="ChatStreamEvent"/>s —
    /// text deltas plus reasoning, tool-call and usage events when the provider reports them.
    /// The underlying <see cref="AgentSession"/> is created lazily and reused so the conversation
    /// keeps context turn to turn.
    /// </summary>
    /// <param name="effort">Reasoning effort for this turn; null leaves it to the model's default
    /// (safe for providers/models that reject the parameter).</param>
    /// <param name="attachments">Pasted or dropped images and files sent along with the message.</param>
    public async IAsyncEnumerable<ChatStreamEvent> SendAsync(
        string userMessage,
        ReasoningEffort? effort = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        IReadOnlyList<Attachment>? attachments = null)
    {
        _session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        var tools = _toolsProvider?.Invoke()?.ToList() ?? [];
        var webSearch = WebSearch && SupportsWebSearch;
        if (webSearch)
            tools.Add(new HostedWebSearchTool());
        var reasoningOptions = _reasoningSummaries || effort is not null
            ? new ReasoningOptions { Output = _reasoningSummaries ? ReasoningOutput.Summary : null, Effort = effort }
            : null;
        var runOptions = tools.Count > 0 || reasoningOptions is not null
            ? new ChatClientAgentRunOptions(new ChatOptions
            {
                Tools = tools.Count > 0 ? [.. tools] : null,
                Reasoning = reasoningOptions,
                // Ask for the pages the search used, not only the ones cited.
#pragma warning disable OPENAI001
                RawRepresentationFactory = webSearch
                    ? _ => new OpenAI.Responses.CreateResponseOptions { IncludedProperties = { OpenAI.Responses.IncludedResponseProperty.WebSearchCallActionSources } }
                    : null,
#pragma warning restore OPENAI001
            })
            : null;

        var updates = attachments is { Count: > 0 }
            ? _agent.RunStreamingAsync([Attachment.BuildMessage(userMessage, attachments)], _session, runOptions, cancellationToken)
            : _agent.RunStreamingAsync(userMessage, _session, runOptions, cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                foreach (var citation in content.Annotations?.OfType<CitationAnnotation>() ?? [])
                {
                    if (citation.Url is { } url)
                        yield return new CitationReported(url.ToString(), citation.Title);
                }

                switch (content)
                {
                    case WebSearchToolCallContent search:
                        yield return new ToolCallStarted(search.CallId, WebSearchToolName,
                            search.Queries is { Count: > 0 } queries ? new Dictionary<string, object?> { ["query"] = string.Join(" / ", queries) } : null);
                        break;
                    case WebSearchToolResultContent results:
                        yield return new ToolCallCompleted(results.CallId, SourcesText(results.Outputs), null);
                        break;
                    case TextContent { Text: { Length: > 0 } text }:
                        yield return new TextDelta(text);
                        break;
                    case TextReasoningContent { Text: { Length: > 0 } reasoning }:
                        yield return new ReasoningDelta(reasoning);
                        break;
                    case FunctionCallContent call:
                        yield return new ToolCallStarted(call.CallId, call.Name, call.Arguments);
                        break;
                    case FunctionResultContent result:
                        yield return new ToolCallCompleted(result.CallId, result.Result, result.Exception);
                        break;
                    case UsageContent { Details: var usage }:
                        yield return new UsageReported(
                            usage.InputTokenCount,
                            usage.OutputTokenCount,
                            usage.TotalTokenCount,
                            usage.ReasoningTokenCount,
                            usage.CachedInputTokenCount);
                        break;
                }
            }
        }
    }

    /// <summary>The pages a web search used, one per line.</summary>
    private static string SourcesText(IList<AIContent>? outputs) =>
        outputs is not { Count: > 0 }
            ? "(no sources returned)"
            : string.Join('\n', outputs.Select(o => o switch
            {
                UriContent uri => uri.Uri.ToString(),
                TextContent text => text.Text,
                _ => o.ToString(),
            }));
}
