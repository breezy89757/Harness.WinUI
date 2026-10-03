// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Harness.Core.Observability;

/// <summary>
/// What the agent does, as OpenTelemetry-style <see cref="Activity"/> spans from one source. Most come
/// from Microsoft.Extensions.AI itself (<see cref="OpenTelemetryChatClient"/>: a <c>chat</c> span per model
/// call with the GenAI semantic-convention attributes, and <c>execute_tool</c> spans from function
/// invocation); Harness.WinUI adds the root span per turn, the raw HTTP exchange under each model call
/// (<see cref="TracingHttpHandler"/>) and approval waits under tool calls. <see cref="TraceRecorder"/>
/// keeps them in a local database; nothing leaves the PC unless the user sets up an exporter.
/// </summary>
public static partial class Telemetry
{
    public const string SourceName = "Harness.WinUI";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Whether message content is recorded and exported: prompts, replies, tool arguments and results,
    /// HTTP bodies. Timing, tokens and status always are. Applies at once, to clients already built too.
    /// </summary>
    public static bool CaptureContent
    {
        get => s_captureContent;
        set
        {
            s_captureContent = value;
            lock (s_instrumented)
            {
                s_instrumented.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var reference in s_instrumented)
                {
                    if (reference.TryGetTarget(out var client))
                        client.EnableSensitiveData = value;
                }
            }
        }
    }

    private static bool s_captureContent = true;
    private static readonly List<WeakReference<OpenTelemetryChatClient>> s_instrumented = [];

    // Operation names (Activity.OperationName). chat / execute_tool / orchestrate_tools come from MEAI.
    public const string OpTurn = "turn";
    public const string OpChat = "chat";
    public const string OpTool = "execute_tool";
    public const string OpAgentLoop = "orchestrate_tools";
    public const string OpHttp = "http";
    public const string OpApproval = "approval";

    // Harness.WinUI's own attributes.
    public const string TagConversation = "harness.conversation_id";
    public const string TagUserMessage = "harness.user_message";
    public const string TagReply = "harness.reply";
    public const string TagToolSource = "harness.tool.source";
    public const string TagDecision = "harness.approval.decision";
    public const string TagHttpRequestBody = "harness.http.request_body";
    public const string TagHttpResponseBody = "harness.http.response_body";

    // Derived when a span is recorded (kept even when content isn't), for the statistics.
    public const string TagToolsOffered = "harness.tools_offered";
    public const string TagToolDefinitionsChars = "harness.tool_definitions_chars";
    public const string TagResultChars = "harness.tool.result_chars";
    public const string TagSkill = "harness.skill";

    // GenAI semantic conventions (as Microsoft.Extensions.AI writes them).
    public const string TagRequestModel = "gen_ai.request.model";
    public const string TagResponseModel = "gen_ai.response.model";
    public const string TagInputTokens = "gen_ai.usage.input_tokens";
    public const string TagOutputTokens = "gen_ai.usage.output_tokens";
    public const string TagCachedTokens = "gen_ai.usage.cache_read.input_tokens";
    public const string TagReasoningTokens = "gen_ai.usage.reasoning.output_tokens";
    public const string TagTimeToFirstChunk = "gen_ai.response.time_to_first_chunk";
    public const string TagToolName = "gen_ai.tool.name";
    public const string TagToolCallId = "gen_ai.tool.call.id";
    public const string TagSystemInstructions = "gen_ai.system_instructions";
    public const string TagInputMessages = "gen_ai.input.messages";
    public const string TagOutputMessages = "gen_ai.output.messages";
    public const string TagToolDefinitions = "gen_ai.tool.definitions";
    public const string TagToolArguments = "gen_ai.tool.call.arguments";
    public const string TagToolResult = "gen_ai.tool.call.result";
    public const string TagHttpMethod = "http.request.method";
    public const string TagUrl = "url.full";
    public const string TagHttpStatus = "http.response.status_code";
    public const string TagErrorType = "error.type";

    /// <summary>Attributes holding message content: stored compressed, separately from the searchable metadata.</summary>
    public static IReadOnlySet<string> ContentTags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TagUserMessage, TagReply, TagHttpRequestBody, TagHttpResponseBody,
        TagSystemInstructions, TagInputMessages, TagOutputMessages, TagToolDefinitions, TagToolArguments, TagToolResult,
    };

    /// <summary>
    /// Adds the GenAI instrumentation (spans and metrics, on <see cref="SourceName"/>) to a chat client.
    /// Message content is included while <see cref="CaptureContent"/> is on (tool calls follow the same
    /// setting); nothing is produced at all while no recorder or exporter listens.
    /// </summary>
    public static IChatClient Instrument(IChatClient client)
    {
        var instrumented = client.AsBuilder()
            .UseOpenTelemetry(sourceName: SourceName, configure: c => c.EnableSensitiveData = CaptureContent)
            .Build();
        if (instrumented.GetService<OpenTelemetryChatClient>() is { } otel)
        {
            lock (s_instrumented)
                s_instrumented.Add(new WeakReference<OpenTelemetryChatClient>(otel));
        }
        return instrumented;
    }

    /// <summary>The HttpClient model calls go through, so each one's raw HTTP exchange is recorded too.</summary>
    public static HttpClient HttpClient { get; } = new(new TracingHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Starts the root span for one user message; it is the current span while the reply streams.</summary>
    public static Activity? StartTurn(string userMessage)
    {
        var activity = Source.StartActivity(OpTurn);
        if (activity is not null && CaptureContent)
            activity.SetTag(TagUserMessage, userMessage);
        return activity;
    }

    /// <summary>Ends a turn: how it went, the reply, and the conversation it belongs to.</summary>
    public static void EndTurn(Activity? turn, string? conversationId, string reply, bool stopped, string? error)
    {
        if (turn is null)
            return;
        turn.SetTag(TagConversation, conversationId);
        turn.SetTag("session.id", conversationId); // groups a conversation's turns in tools such as Langfuse
        if (CaptureContent)
            turn.SetTag(TagReply, reply);
        if (error is not null)
            turn.SetStatus(ActivityStatusCode.Error, error);
        else if (stopped)
            turn.SetStatus(ActivityStatusCode.Error, StatusCancelled);
        else
            turn.SetStatus(ActivityStatusCode.Ok);
        turn.Stop();
    }

    /// <summary>Status description of spans the user stopped: shown as stopped, not as failures.</summary>
    public const string StatusCancelled = "cancelled";

    /// <summary>
    /// Text as it is stored: long base64 runs (attached images in requests, generated images in
    /// responses) are replaced by their size, so a screenshot doesn't put megabytes into every later
    /// request's record.
    /// </summary>
    public static string Redact(string text) =>
        Base64Run().Replace(text, m => $"‹base64, {m.Length * 3 / 4 / 1024:N0} KB omitted›");

    [GeneratedRegex("[A-Za-z0-9+/]{2048,}={0,2}")]
    private static partial Regex Base64Run();
}
