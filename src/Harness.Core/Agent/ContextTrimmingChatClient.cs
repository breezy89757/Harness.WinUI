// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace Harness.Core.Agent;

/// <summary>
/// Keeps a long coding session from re-sending every file it ever read: once the tool results in the conversation add up to
/// more than <see cref="StartAtChars"/>, the oldest large ones are replaced, in what is sent to the model, by one line saying
/// what they were (the conversation shown and saved is not touched; the model can call the tool again if it needs the
/// content). Trimming only grows: a result trimmed once stays trimmed, and several go at a time, so the start of the request
/// stays the same between calls and the provider's prompt cache keeps working.
/// </summary>
/// <param name="enabled">Asked on every call; false sends the conversation untouched.</param>
public sealed class ContextTrimmingChatClient(IChatClient inner, Func<bool> enabled) : DelegatingChatClient(inner)
{
    /// <summary>Total size of tool results above which trimming starts.</summary>
    public const int StartAtChars = 160_000;

    /// <summary>Trimming goes on until the results still shown fit in this.</summary>
    public const int TrimDownToChars = 80_000;

    /// <summary>Results shorter than this aren't worth a placeholder.</summary>
    private const int MinTrimmedChars = 1500;

    /// <summary>The newest results are always kept whole.</summary>
    private const int KeepNewest = 6;

    private readonly HashSet<string> _trimmed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Trim(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(Trim(messages), options, cancellationToken);

    private IEnumerable<ChatMessage> Trim(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        if (!enabled())
            return list;

        // Every tool result in order, with its size.
        var results = new List<(string CallId, int Chars)>();
        foreach (var message in list)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
                results.Add((result.CallId, SizeOf(result)));
        }

        lock (_lock)
        {
            var kept = results.Where(r => !_trimmed.Contains(r.CallId)).Sum(r => (long)r.Chars);
            if (kept > StartAtChars)
            {
                // Oldest first, never the newest few, until what is left fits.
                foreach (var (callId, chars) in results.Take(Math.Max(0, results.Count - KeepNewest)))
                {
                    if (kept <= TrimDownToChars)
                        break;
                    if (chars >= MinTrimmedChars && _trimmed.Add(callId))
                        kept -= chars;
                }
            }

            if (_trimmed.Count == 0)
                return list;
        }

        var sizes = results.ToDictionary(r => r.CallId, r => r.Chars, StringComparer.Ordinal);
        var changed = new List<ChatMessage>(list.Count);
        foreach (var message in list)
        {
            if (!message.Contents.OfType<FunctionResultContent>().Any(r => IsTrimmed(r.CallId)))
            {
                changed.Add(message);
                continue;
            }

            var copy = message.Clone();
            copy.Contents = [.. message.Contents.Select(content =>
                content is FunctionResultContent result && IsTrimmed(result.CallId)
                    ? new FunctionResultContent(result.CallId, $"[earlier tool result left out to save space: {sizes.GetValueOrDefault(result.CallId):N0} characters. Call the tool again if you need it.]")
                    : content)];
            changed.Add(copy);
        }
        return changed;
    }

    private bool IsTrimmed(string callId)
    {
        lock (_lock)
            return _trimmed.Contains(callId);
    }

    private static int SizeOf(FunctionResultContent result) =>
        result.Result switch
        {
            null => 0,
            string text => text.Length,
            System.Text.Json.JsonElement json => json.GetRawText().Length,
            var other => other.ToString()?.Length ?? 0,
        };
}
