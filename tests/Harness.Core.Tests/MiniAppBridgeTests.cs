// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json.Nodes;
using Harness.Core.MiniApps;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tests;

public class MiniAppBridgeTests
{
    private const string AskingPage = """<title>Ask</title><script type="application/json" id="harness-app">{"permissions":["llm"]}</script>""";

    private sealed class FakeClient(string reply = "hello", string? streamError = null) : IChatClient
    {
        public List<IList<ChatMessage>> Calls { get; } = [];
        public ChatOptions? LastOptions { get; private set; }
        public TaskCompletionSource? Hold { get; set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            LastOptions = options;
            if (Hold is { } hold)
                await hold.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 } };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            if (reply.Length == 0)
            {
                if (streamError is not null)
                    yield return new ChatResponseUpdate { Contents = [new ErrorContent(streamError)] };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "he");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "llo");
            yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2 })] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();
        public MiniAppStore Store { get; }
        public MiniAppInfo App { get; set; }
        public FakeClient Client { get; set; } = new();
        public List<(string? Model, long In, long Out)> Usage { get; } = [];
        public List<string> Links { get; } = [];
        public List<JsonObject> Sent { get; } = [];
        public MiniAppBridge Bridge { get; }
        public string AuditPath => Path.Combine(_dir.Path, "audit.jsonl");

        public Rig(string html = AskingPage, bool approve = true, bool withModel = true)
        {
            Store = new MiniAppStore(Path.Combine(_dir.Path, "apps"));
            App = Store.Install(html, null);
            if (approve)
                App = Store.Approve(App.Id)!;
            Bridge = new MiniAppBridge(
                App.Id, Store, () => withModel ? (Client, "test-model") : (null, null),
                (m, i, o) => Usage.Add((m, i, o)), Links.Add, new MiniAppAudit(AuditPath));
        }

        public async Task SendAsync(object request) =>
            await Bridge.HandleAsync(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.ToJsonString(), json =>
            {
                Sent.Add(JsonNode.Parse(json)!.AsObject());
                return Task.CompletedTask;
            });

        public string? ErrorCode => Sent.LastOrDefault()?["error"]?["code"]?.GetValue<string>();

        public void Dispose()
        {
            Bridge.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public async Task Chat_answers_and_records_usage()
    {
        using var rig = new Rig();
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });

        var reply = Assert.Single(rig.Sent);
        Assert.Equal(1, reply["id"]!.GetValue<int>());
        Assert.Equal("hello", reply["result"]!["text"]!.GetValue<string>());
        Assert.Equal([("test-model", 11L, 7L)], rig.Usage);
    }

    [Fact]
    public async Task Each_call_is_a_turn_in_the_observability_records()
    {
        var turns = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == Harness.Core.Observability.Telemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (turns) turns.Add(a); },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var rig = new Rig();
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi there" } } } });

        var turn = Assert.Single(turns, a => a.OperationName == Harness.Core.Observability.Telemetry.OpTurn && (a.GetTagItem(Harness.Core.Observability.Telemetry.TagConversation) as string) == "miniapp:" + rig.App.Id);
        Assert.Equal(System.Diagnostics.ActivityStatusCode.Ok, turn.Status);
    }

    [Fact]
    public async Task Chat_streams_pieces_then_the_whole()
    {
        using var rig = new Rig();
        await rig.SendAsync(new { id = "a", method = "llm.chat", @params = new { stream = true, messages = new[] { new { role = "user", content = "hi" } } } });

        Assert.Equal(["he", "llo"], rig.Sent.Where(m => m["delta"] is not null).Select(m => m["delta"]!.GetValue<string>()));
        var last = rig.Sent[^1];
        Assert.Equal("a", last["id"]!.GetValue<string>());
        Assert.Equal("hello", last["result"]!["text"]!.GetValue<string>());
        Assert.Equal([("test-model", 5L, 2L)], rig.Usage);
    }

    [Fact]
    public async Task Chat_without_asking_for_llm_is_refused()
    {
        using var rig = new Rig("<title>Plain</title>");
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Equal(MiniAppErrors.PermissionNotRequested, rig.ErrorCode);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task Chat_before_the_person_allowed_it_is_refused()
    {
        using var rig = new Rig(approve: false);
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Equal(MiniAppErrors.PermissionDenied, rig.ErrorCode);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_new_version_has_to_be_allowed_again()
    {
        using var rig = new Rig();
        rig.Store.Update(rig.App.Id, AskingPage + "<p>changed</p>");
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Equal(MiniAppErrors.PermissionDenied, rig.ErrorCode);

        rig.Store.Approve(rig.App.Id);
        await rig.SendAsync(new { id = 2, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Null(rig.ErrorCode);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "rate limited")]
    [InlineData(false, null)]
    public async Task An_empty_answer_is_an_error_not_a_success(bool stream, string? providerError)
    {
        using var rig = new Rig();
        rig.Client = new FakeClient("", providerError);
        var bridge = new MiniAppBridge(rig.App.Id, rig.Store, () => (rig.Client, "m"), (_, _, _) => { }, _ => { }, new MiniAppAudit(rig.AuditPath));
        await bridge.HandleAsync(
            "{\"id\":1,\"method\":\"llm.chat\",\"params\":{\"stream\":" + stream.ToString().ToLowerInvariant() + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}}", json =>
        {
            rig.Sent.Add(JsonNode.Parse(json)!.AsObject());
            return Task.CompletedTask;
        });
        bridge.Dispose();

        Assert.Equal(MiniAppErrors.Failed, rig.ErrorCode);
        if (providerError is not null)
            Assert.Contains(providerError, rig.Sent[^1]["error"]!["message"]!.GetValue<string>());
        Assert.Empty(rig.Usage);
    }

    [Fact]
    public async Task Chat_without_a_model_says_so()
    {
        using var rig = new Rig(withModel: false);
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Equal(MiniAppErrors.NoModel, rig.ErrorCode);
    }

    [Theory]
    [InlineData("""{"id":1,"method":"llm.chat","params":{}}""")]
    [InlineData("""{"id":1,"method":"llm.chat","params":{"messages":[]}}""")]
    [InlineData("""{"id":1,"method":"llm.chat","params":{"messages":[{"role":"system","content":"x"}]}}""")]
    [InlineData("""{"id":1,"method":"llm.chat","params":{"messages":[{"role":"user"}]}}""")]
    public async Task Chat_with_bad_params_is_refused(string request)
    {
        using var rig = new Rig();
        await rig.Bridge.HandleAsync(request, json =>
        {
            rig.Sent.Add(JsonNode.Parse(json)!.AsObject());
            return Task.CompletedTask;
        });
        Assert.Equal(MiniAppErrors.InvalidParams, rig.ErrorCode);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task Chat_too_long_is_refused_and_output_is_capped()
    {
        using var rig = new Rig();
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = new string('x', MiniAppBridge.MaxChars + 1) } } } });
        Assert.Equal(MiniAppErrors.InvalidParams, rig.ErrorCode);

        await rig.SendAsync(new { id = 2, method = "llm.chat", @params = new { maxTokens = 1_000_000, messages = new[] { new { role = "user", content = "hi" } } } });
        Assert.Equal(MiniAppBridge.MaxOutputTokens, rig.Client.LastOptions!.MaxOutputTokens);
    }

    [Fact]
    public async Task A_second_call_while_one_runs_is_busy()
    {
        using var rig = new Rig();
        rig.Client.Hold = new TaskCompletionSource();
        var request = new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "hi" } } } };
        var first = rig.SendAsync(request);
        await rig.SendAsync(new { id = 2, method = "llm.chat", @params = request.@params });
        Assert.Equal(MiniAppErrors.Busy, rig.ErrorCode);

        rig.Client.Hold.SetResult();
        await first;
        Assert.Equal(2, rig.Sent.Count);
    }

    [Fact]
    public async Task Unknown_method_and_garbage_are_answered_or_ignored()
    {
        using var rig = new Rig();
        await rig.SendAsync(new { id = 1, method = "fs.read", @params = new { path = "C:\\x" } });
        Assert.Equal(MiniAppErrors.MethodNotFound, rig.ErrorCode);

        var before = rig.Sent.Count;
        await rig.Bridge.HandleAsync("not json", _ => Task.CompletedTask);
        await rig.Bridge.HandleAsync("""{"method":"llm.chat"}""", _ => Task.CompletedTask); // no id: nothing to answer
        Assert.Equal(before, rig.Sent.Count);
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("file:///C:/Windows/system32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("calc.exe", false)]
    public async Task Only_web_links_are_opened(string url, bool opened)
    {
        using var rig = new Rig("<title>Plain</title>");
        await rig.SendAsync(new { id = 1, method = "open.link", @params = new { url } });
        Assert.Equal(opened ? [new Uri(url).AbsoluteUri] : [], rig.Links);
    }

    [Fact]
    public async Task Audit_records_calls_without_their_text()
    {
        using var rig = new Rig();
        await rig.SendAsync(new { id = 1, method = "llm.chat", @params = new { messages = new[] { new { role = "user", content = "my secret question" } } } });
        var text = File.ReadAllText(rig.AuditPath);
        Assert.Contains("llm.chat", text);
        Assert.Contains(rig.App.Id, text);
        Assert.DoesNotContain("secret", text);
    }
}
