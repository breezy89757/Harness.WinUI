// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace Harness.Core.Agent;

/// <summary>
/// Adds extra instructions (the user's own, see <see cref="CustomInstructions"/>, and their memories) to the instructions of
/// every model call. They are asked for each time, not once when the agent is built, so edits take effect on the next message.
/// </summary>
/// <param name="extra">Makes the text from the first thing the user said in the conversation (null if it isn't known).</param>
public sealed class ExtraInstructionsChatClient(IChatClient inner, Func<string?, string?> extra) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        return base.GetResponseAsync(list, WithExtra(list, options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        return base.GetStreamingResponseAsync(list, WithExtra(list, options), cancellationToken);
    }

    private ChatOptions? WithExtra(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var first = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;
        var text = extra(first);
        if (string.IsNullOrEmpty(text))
            return options;

        // A copy: the caller's options are shared across calls and must not collect the text.
        var copy = options?.Clone() ?? new ChatOptions();
        copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? text : copy.Instructions + "\n\n" + text;
        return copy;
    }
}
