// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Harness.Core.Observability;

/// <summary>
/// Answers questions about the recorded traces ("which step is slowest?", "which tools could be turned
/// off?") with the user's own model, which reads the records through three read-only tools. The
/// analysis itself is not recorded (the client passed in should be uninstrumented).
/// </summary>
public sealed class TraceAnalyst(TraceStore store)
{
    private const string Instructions = """
        You analyze the observability records of Harness.WinUI, a desktop AI agent, to help its user understand
        performance, cost, failures and how tools and skills are used. A turn is one user message and all the
        agent's work on it: model calls (with tokens, time to first token, cost), tool calls (with where the tool
        comes from: built-in, skill, or an MCP server; arguments and results) and waits for the user's approval.

        - Fetch data with the tools; never guess numbers. Start with get_statistics, then look at specific turns
          (list_turns, get_turn) when that helps explain something.
        - Be concrete: name the turns (time and message), steps, tools and figures behind each finding.
        - Suggest specific improvements when the data supports them: tools that are offered but never used (their
          definitions cost input tokens on every call), slow or failing tools and MCP servers, unusually slow model
          calls, large tool results that fill the context, long approval waits, a low prompt-cache hit rate.
        - CacheHitRate is the share of input tokens the provider served from its prompt cache (cheaper and faster).
          null means the provider doesn't report it: say so, don't call it 0%. A low rate while the same long prefix
          (system instructions, tool definitions) is sent on every call means that prefix probably changes between
          calls or the provider's cache isn't kicking in; say what the data shows and what to check, without
          claiming a cause you can't see.
        - Reply in the language of the question; for Chinese use Traditional Chinese with Taiwan terminology.
        - Plain text only, no Markdown symbols (no #, **, tables). Use short paragraphs and lines starting with "• ".
        """;

    private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Streams the answer to <paramref name="question"/> about the last <paramref name="days"/> days of records.</summary>
    public async IAsyncEnumerable<string> AskAsync(
        IChatClient client, string question, int days, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var agent = client.AsBuilder().UseFunctionInvocation().Build();
        var options = new ChatOptions { Instructions = Instructions, Tools = CreateTools(days) };
        var messages = new List<ChatMessage> { new(ChatRole.User, $"(Records from the last {days} days.)\n\n{question}") };
        await foreach (var update in agent.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    private List<AITool> CreateTools(int defaultDays) =>
    [
        AIFunctionFactory.Create(
            ([Description("Days back from now.")] int? days) =>
                JsonSerializer.Serialize(store.Statistics(Since(days ?? defaultDays)), s_json),
            "get_statistics",
            "Totals for the period (including cached input tokens and the prompt-cache hit rate), then per model (calls, failures, median and P95 duration, time to first token, tokens, cache hit rate, cost, " +
            "size of the tool definitions sent per call), per tool (times offered to the model vs. called, failures, average and " +
            "slowest duration, average result size in characters, source), per skill, and per approval decision."),

        AIFunctionFactory.Create(
            ([Description("Days back from now.")] int? days,
             [Description("Only turns whose message, tool or model contains this text.")] string? search,
             [Description("Only turns with an error.")] bool? failedOnly,
             [Description("At most this many turns, newest first (default 30).")] int? limit) =>
                JsonSerializer.Serialize(
                    store.Turns(Since(days ?? defaultDays), search, failedOnly == true, Math.Clamp(limit ?? 30, 1, 200))
                        .Select(t => new
                        {
                            t.TraceId, Started = t.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), DurationMs = Math.Round(t.DurationMs),
                            t.Failed, t.Status, Message = Cut(t.UserMessage, 200), t.Model, t.ModelCalls, t.ToolCalls, t.ToolNames,
                            t.InputTokens, t.OutputTokens, t.Cost,
                        }),
                    s_json),
            "list_turns",
            "Recorded turns, newest first: when, how long, whether it failed, the user's message, model, number of model and tool calls, tools used, tokens, cost."),

        AIFunctionFactory.Create(
            ([Description("trace id from list_turns.")] string traceId) => DescribeTurn(traceId),
            "get_turn",
            "One turn step by step: each model call, tool call and approval wait with its start (ms from the turn's start), duration, " +
            "status, tokens and cost; tool arguments and results, the user's message and the reply (shortened)."),
    ];

    private string DescribeTurn(string traceId)
    {
        var spans = store.Trace(traceId);
        var turn = spans.FirstOrDefault(s => s.Operation == Telemetry.OpTurn);
        if (turn is null)
            return "No turn with that id.";

        var steps = spans
            .Where(s => s.Operation is Telemetry.OpChat or Telemetry.OpTool or Telemetry.OpApproval)
            .Select(s => new
            {
                Step = s.Operation,
                s.Name,
                StartMs = Math.Round((s.StartedAt - turn.StartedAt).TotalMilliseconds),
                DurationMs = Math.Round(s.DurationMs),
                s.Failed,
                s.Status,
                s.Model,
                s.InputTokens,
                s.OutputTokens,
                s.CachedTokens,
                TimeToFirstTokenMs = s.TimeToFirstChunkMs is { } t ? Math.Round(t) : (double?)null,
                s.Cost,
                s.ToolName,
                s.ToolSource,
                Decision = s.Attributes.GetValueOrDefault(Telemetry.TagDecision),
                ToolsOffered = s.Attributes.GetValueOrDefault(Telemetry.TagToolsOffered),
                Arguments = Cut(s.Content?.GetValueOrDefault(Telemetry.TagToolArguments), 1000),
                Result = Cut(s.Content?.GetValueOrDefault(Telemetry.TagToolResult), 1500),
                ResultChars = s.Attributes.GetValueOrDefault(Telemetry.TagResultChars),
                Output = s.Operation == Telemetry.OpChat ? Cut(s.Content?.GetValueOrDefault(Telemetry.TagOutputMessages), 1000) : null,
            });

        return JsonSerializer.Serialize(new
        {
            turn.TraceId,
            Started = turn.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            DurationMs = Math.Round(turn.DurationMs),
            turn.Failed,
            turn.Status,
            Message = Cut(turn.Content?.GetValueOrDefault(Telemetry.TagUserMessage), 1500),
            Reply = Cut(turn.Content?.GetValueOrDefault(Telemetry.TagReply), 1500),
            Steps = steps,
        }, s_json);
    }

    private static DateTimeOffset Since(int days) => DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));

    private static string? Cut(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + $"…(+{text.Length - max} chars)";
}
