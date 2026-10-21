// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace Harness.Core.Tools;

/// <summary>
/// Lets the tool calls of one reply overlap where that is safe. Calls that only read (read_file, search_text, spawn_agent …) run at
/// the same time; every other call waits for the calls that came before it and then runs alone, so changes still happen one at a
/// time and in the order the model asked for them. Without it, the model's calls run one after the other.
/// </summary>
public sealed class ToolGate
{
    private readonly Lock _lock = new();
    private Task _lastExclusive = Task.CompletedTask;
    private List<Task> _shared = [];

    /// <summary>Runs <paramref name="run"/> when its turn comes: <paramref name="shared"/> calls overlap each other, the others run alone.</summary>
    public async Task<T> RunAsync<T>(bool shared, Func<Task<T>> run, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] before;
        lock (_lock)
        {
            if (shared)
            {
                before = [_lastExclusive];
                _shared.RemoveAll(t => t.IsCompleted);
                _shared.Add(done.Task);
            }
            else
            {
                before = [.. _shared, _lastExclusive];
                _shared = [];
                _lastExclusive = done.Task;
            }
        }

        try
        {
            // The tasks waited for only ever complete normally, and a call that is cancelled still lets the ones behind it go on.
            await Task.WhenAll(before).WaitAsync(cancellationToken).ConfigureAwait(false);
            return await run().ConfigureAwait(false);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Wraps <paramref name="tool"/> so it goes through this gate; tools that aren't functions are returned as they are.</summary>
    public AITool Wrap(AITool tool, bool shared) =>
        tool is AIFunction function ? new GatedFunction(function, this, shared) : tool;

    private sealed class GatedFunction(AIFunction inner, ToolGate gate, bool shared) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await gate.RunAsync(shared, async () => await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
}
