// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Agent;
using Harness.Core.Tools;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task Calls_that_read_overlap()
    {
        var gate = new ToolGate();
        var running = 0;
        var most = 0;
        async Task<int> Read()
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(100);
            Interlocked.Decrement(ref running);
            return 1;
        }

        await Task.WhenAll(gate.RunAsync(true, Read, default), gate.RunAsync(true, Read, default), gate.RunAsync(true, Read, default));

        Assert.Equal(3, most);
    }

    [Fact]
    public async Task A_change_waits_for_the_reads_before_it_and_the_reads_after_it_wait_for_the_change()
    {
        var gate = new ToolGate();
        var log = new List<string>();
        Func<Task<int>> Step(string name, int delay) => async () =>
        {
            lock (log) log.Add("start " + name);
            await Task.Delay(delay);
            lock (log) log.Add("end " + name);
            return 0;
        };

        var calls = new[]
        {
            gate.RunAsync(true, Step("read1", 150), default),
            gate.RunAsync(false, Step("write", 50), default),
            gate.RunAsync(true, Step("read2", 10), default),
            gate.RunAsync(false, Step("write2", 10), default),
        };
        await Task.WhenAll(calls);

        Assert.Equal(["start read1", "end read1", "start write", "end write", "start read2", "end read2", "start write2", "end write2"], log);
    }

    [Fact]
    public async Task A_call_that_is_cancelled_while_waiting_does_not_hold_up_the_others()
    {
        var gate = new ToolGate();
        using var cancel = new CancellationTokenSource();
        var first = gate.RunAsync(false, async () => { await Task.Delay(100); return 1; }, default);
        var cancelled = gate.RunAsync(false, () => Task.FromResult(2), cancel.Token);
        var last = gate.RunAsync(false, () => Task.FromResult(3), default);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, await first);
        Assert.Equal(3, await last);
    }

    // The model asks for the same things as the real one would: a batch of calls in one reply.
    private sealed class BatchClient(params (string Name, string Id)[] batch) : IChatClient
    {
        private int _turns;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_turns++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. batch.Select(c => (AIContent)new FunctionCallContent(c.Id, c.Name, new Dictionary<string, object?>()))]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task In_a_reply_the_reads_overlap_and_the_changes_keep_their_order()
    {
        var gate = new ToolGate();
        var log = new List<string>();
        var running = 0;
        var most = 0;
        AITool Tool(string name, bool shared, int delay) => gate.Wrap(AIFunctionFactory.Create(async () =>
        {
            lock (log) log.Add("start " + name);
            if (shared)
                most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(delay);
            if (shared)
                Interlocked.Decrement(ref running);
            lock (log) log.Add("end " + name);
            return "ok";
        }, name), shared);

        var tools = new[] { Tool("read_a", true, 150), Tool("read_b", true, 150), Tool("edit", false, 20), Tool("read_c", true, 10) };
        var client = new BatchClient(("read_a", "1"), ("read_b", "2"), ("edit", "3"), ("read_c", "4"));
        var session = new ChatSession(client, new Harness.Core.Config.HarnessOptions { Instructions = "x" }, () => tools);

        await foreach (var _ in session.SendAsync("go")) { }

        Assert.Equal(2, most);
        Assert.True(log.IndexOf("end read_a") < log.IndexOf("start edit"));
        Assert.True(log.IndexOf("end read_b") < log.IndexOf("start edit"));
        Assert.True(log.IndexOf("end edit") < log.IndexOf("start read_c"));
    }

    private sealed class SpyClient : IChatClient
    {
        private int _turns;

        public List<ReasoningEffort?> Efforts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Efforts.Add(options?.Reasoning?.Effort);
            return Task.FromResult(_turns++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "list_directory", new Dictionary<string, object?> { ["path"] = "." })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "found it")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_helper_reports_what_it_does_and_uses_the_effort_it_is_given()
    {
        using var dir = new TempDir();
        dir.Write("a.txt", "hello");
        var client = new SpyClient();
        var events = new List<HelperEvent>();
        var helper = HelperAgentTool.Create(
            () => client, () => new Harness.Core.Files.Sandbox(dir.Path), new CountingApprover(), dir.NewPermissions(),
            progress: events.Add, effort: () => ReasoningEffort.Low);

        var report = await new[] { helper }.CallAsync("spawn_agent", new() { ["task"] = "what is in the folder?" });

        Assert.Contains("found it", report);
        Assert.Equal([HelperEventKind.Started, HelperEventKind.Step, HelperEventKind.Finished], events.Select(e => e.Kind));
        Assert.Equal("what is in the folder?", events[0].Text);
        Assert.StartsWith("list_directory", events[1].Text);
        Assert.Equal(1, events[2].Steps);
        Assert.All(events, e => Assert.Equal(events[0].CallId, e.CallId));
        Assert.All(client.Efforts, effort => Assert.Equal(ReasoningEffort.Low, effort));
    }

    // A model that never answers, so the helper is still working when it is stopped.
    private sealed class HangingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<ChatResponse>(_ => throw new OperationCanceledException(cancellationToken), TaskScheduler.Default);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task One_helper_can_be_stopped_and_still_reports()
    {
        using var dir = new TempDir();
        var registry = new HelperRegistry();
        var events = new List<HelperEvent>();
        var started = new TaskCompletionSource<string>();
        var helper = HelperAgentTool.Create(
            () => new HangingClient(), () => new Harness.Core.Files.Sandbox(dir.Path), new CountingApprover(), dir.NewPermissions(),
            progress: e => { events.Add(e); if (e.Kind == HelperEventKind.Started) started.TrySetResult(e.CallId); }, registry: registry);

        var running = new[] { helper }.CallAsync("spawn_agent", new() { ["task"] = "never answered" });
        var callId = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(registry.Stop(callId));
        var report = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("stopped by you", report);
        Assert.Equal(HelperEventKind.Finished, events[^1].Kind);
        Assert.Contains("stopped by you", events[^1].Text);
        Assert.False(registry.Stop(callId)); // it is gone now
    }

    [Fact]
    public void Explore_hands_the_question_to_helpers()
    {
        var request = CodingPrompt.ExploreRequest("how does X work?");

        Assert.Contains("spawn_agent", request);
        Assert.EndsWith("how does X work?", request.TrimEnd());
    }}
