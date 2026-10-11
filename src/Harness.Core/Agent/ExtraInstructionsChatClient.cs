// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace Harness.Core.Agent;

/// <summary>
/// Adds extra instructions (the user's own, see <see cref="CustomInstructions"/>) to the instructions of every model
/// call. They are asked for each time, not once when the agent is built, so edits take effect on the next message.
/// </summary>
public sealed class ExtraInstructionsChatClient(IChatClient inner, Func<string?> extra) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithExtra(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithExtra(options), cancellationToken);

    private ChatOptions? WithExtra(ChatOptions? options)
    {
        var text = extra();
        if (string.IsNullOrEmpty(text))
            return options;

        // A copy: the caller's options are shared across calls and must not collect the text.
        var copy = options?.Clone() ?? new ChatOptions();
        copy.Instructions = string.IsNullOrEmpty(copy.Instructions) ? text : copy.Instructions + "\n\n" + text;
        return copy;
    }
}
