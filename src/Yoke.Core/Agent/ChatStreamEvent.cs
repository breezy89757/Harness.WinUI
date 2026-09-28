// Yoke — Licensed under the MIT License.

namespace Yoke.Core.Agent;

/// <summary>One observable step of an assistant turn, surfaced so the UI can show more than raw text.</summary>
public abstract record ChatStreamEvent;

public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>Model "thinking" text, when the provider streams it (Chat Completions endpoints usually don't).</summary>
public sealed record ReasoningDelta(string Text) : ChatStreamEvent;

public sealed record ToolCallStarted(string CallId, string Name, IDictionary<string, object?>? Arguments) : ChatStreamEvent;

public sealed record ToolCallCompleted(string CallId, object? Result, Exception? Exception) : ChatStreamEvent;

/// <summary>Usage for one model call. A turn with tool calls makes several calls, so sum them with <see cref="Add"/>.</summary>
public sealed record UsageReported(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? ReasoningTokens,
    long? CachedInputTokens) : ChatStreamEvent
{
    public UsageReported Add(UsageReported other) => new(
        Sum(InputTokens, other.InputTokens),
        Sum(OutputTokens, other.OutputTokens),
        Sum(TotalTokens, other.TotalTokens),
        Sum(ReasoningTokens, other.ReasoningTokens),
        Sum(CachedInputTokens, other.CachedInputTokens));

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
