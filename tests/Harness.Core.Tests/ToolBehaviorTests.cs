// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Agent;
using Harness.Core.Files;
using Harness.Core.History;
using Harness.Core.Tools;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tests;

public sealed class TextDiffTests
{
    [Fact]
    public void AChangedLine_ReadsAsRemovedThenAdded()
    {
        var diff = TextDiff.Unified("a\nb\nc\n", "a\nB\nc\n", context: 1);

        Assert.Equal("  a\n- b\n+ B\n  c", diff);
    }

    [Fact]
    public void NoChange_IsEmpty() => Assert.Equal(string.Empty, TextDiff.Unified("same\n", "same\n"));

    [Fact]
    public void Count_AddsUpAddedAndRemovedLines()
    {
        var (added, removed) = TextDiff.Count("a\nb\n", "a\nx\ny\n");

        Assert.Equal(2, added);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void UsesPlainNewlines_SoAPageShowsOneLinePerLine() => Assert.DoesNotContain('\r', TextDiff.Unified("a\n", "b\n"));
}

public sealed class GitIgnoreTests
{
    [Theory]
    [InlineData("out/result.cs", false, true)]       // a folder rule takes everything below it
    [InlineData("src/app.log", false, true)]         // *.log at any depth
    [InlineData("src/keep.log", false, false)]       // brought back with !
    [InlineData("docs/a/b/notes.tmp", false, true)]  // docs/**/*.tmp
    [InlineData("docs/a/notes.md", false, false)]
    [InlineData("src/app.cs", false, false)]
    [InlineData("pkg/generated", true, true)]        // a .gitignore in a subfolder
    [InlineData("generated/x.cs", false, false)]     // ...applies only below that folder
    public void Rules(string path, bool isDirectory, bool ignored)
    {
        using var dir = new TempDir();
        dir.Write(".gitignore", "# comment\n/out/\n*.log\n!keep.log\ndocs/**/*.tmp\n");
        dir.Write("pkg/.gitignore", "generated/\n");

        Assert.Equal(ignored, new GitIgnore(dir.Path).IsIgnored(path, isDirectory));
    }
}

public sealed class PermissionTests
{
    private static async Task<int> AskedAfterWriteAsync(ToolPermissionStore permissions, TempDir dir)
    {
        var approver = new CountingApprover();
        var tools = FileTools.Create(new Sandbox(dir.Path), approver, permissions).ToList();
        await tools.CallAsync("write_file", new() { ["path"] = "a.txt", ["content"] = "x" });
        return approver.Asked;
    }

    [Fact]
    public async Task Yolo_StopsTheQuestions()
    {
        using var dir = new TempDir();
        var permissions = dir.NewPermissions();

        Assert.Equal(1, await AskedAfterWriteAsync(permissions, dir));
        permissions.AutoApprove = true;
        Assert.Equal(0, await AskedAfterWriteAsync(permissions, dir));
    }

    [Fact]
    public async Task Yolo_StillAsksAboutChangesToTheAppItself()
    {
        using var dir = new TempDir();
        var permissions = dir.NewPermissions();
        permissions.AutoApprove = true;
        var approver = new CountingApprover();
        var tool = AppearanceTool.Create(approver, permissions, ["usage"], (_, _) => Task.FromResult("done"));

        await ((AIFunction)tool).InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["setting"] = "text_size", ["value"] = "120" }));

        Assert.Equal(1, approver.Asked);
    }

    [Fact]
    public void Yolo_AndReadOnly_ExcludeEachOther()
    {
        using var dir = new TempDir();
        var permissions = dir.NewPermissions();

        permissions.AutoApprove = true;
        permissions.ReadOnlyMode = true;
        Assert.False(permissions.AutoApprove);

        permissions.AutoApprove = true;
        Assert.False(permissions.ReadOnlyMode);
    }

    [Fact]
    public void AlwaysAllow_MadeInAFolder_BelongsToThatFolder()
    {
        using var dir = new TempDir();
        var permissions = dir.NewPermissions();

        permissions.Scope = @"C:\proj-a";
        permissions.AllowAlways("files", "write_file");
        Assert.True(permissions.IsAlwaysAllowed("files", "write_file"));

        permissions.Scope = @"C:\proj-b";
        Assert.False(permissions.IsAlwaysAllowed("files", "write_file"));
        permissions.Scope = null;
        Assert.False(permissions.IsAlwaysAllowed("files", "write_file"));

        Assert.Contains(permissions.ListAll(), e => e.Folder == @"C:\proj-a" && e.ToolName == "write_file");
        permissions.Remove(@"C:\proj-a", "files", "write_file");
        permissions.Scope = @"C:\proj-a";
        Assert.False(permissions.IsAlwaysAllowed("files", "write_file"));
    }

    [Fact]
    public void GlobalAlwaysAllow_AppliesInEveryFolder()
    {
        using var dir = new TempDir();
        var permissions = dir.NewPermissions();
        permissions.AllowAlways("files", "write_file");

        permissions.Scope = @"C:\anywhere";

        Assert.True(permissions.IsAlwaysAllowed("files", "write_file"));
    }
}

public sealed class TaskToolTests
{
    [Fact]
    public async Task UpdateTasks_ReplacesTheListAndNormalizesStatuses()
    {
        var list = new TaskList();
        var tool = TaskTools.Create(list);

        var result = await new[] { tool }.CallAsync(TaskTools.Name, new()
        {
            ["tasks"] = new List<TaskInput> { new("Read the code", "done"), new("Fix it", "In Progress"), new("Test", "whatever") },
        });

        Assert.Equal([TaskItem.Completed, TaskItem.InProgress, TaskItem.Pending], list.Items.Select(i => i.Status));
        Assert.Contains("1 of 3 completed", result);
        Assert.Contains("Next: Fix it", result);
    }
}

public sealed class ContextTrimmingTests
{
    private sealed class Capture : IChatClient
    {
        public IReadOnlyList<ChatMessage> Last { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Last = [.. messages];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Last = [.. messages];
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static List<ChatMessage> History(int results)
    {
        var list = new List<ChatMessage> { new(ChatRole.User, "read everything") };
        for (var i = 1; i <= results; i++)
        {
            list.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c" + i, "read_file")]));
            list.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c" + i, new string('x', 30_000))]));
        }
        return list;
    }

    private static string Shape(IReadOnlyList<ChatMessage> sent) => string.Concat(sent
        .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
        .Select(r => r.Result is string s && s.StartsWith("[earlier") ? 'T' : 'F'));

    private static async Task SendAsync(IChatClient client, List<ChatMessage> messages)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(messages))
        {
        }
    }

    [Fact]
    public async Task BelowTheThreshold_NothingIsTrimmed()
    {
        var inner = new Capture();
        await SendAsync(new ContextTrimmingChatClient(inner, () => true), History(4));

        Assert.Equal("FFFF", Shape(inner.Last));
    }

    [Fact]
    public async Task AboveIt_TheOldestAreTrimmed_TheNewestSixKept_AndTrimmedStaysTrimmed()
    {
        var inner = new Capture();
        var client = new ContextTrimmingChatClient(inner, () => true);

        await SendAsync(client, History(8));
        Assert.Equal("TTFFFFFF", Shape(inner.Last));

        await SendAsync(client, History(9));
        Assert.StartsWith("TT", Shape(inner.Last));
        Assert.EndsWith("FFFFFF", Shape(inner.Last));
    }

    [Fact]
    public async Task WhenDisabled_TheConversationIsSentWhole_AndTheOriginalIsNeverChanged()
    {
        var inner = new Capture();
        var messages = History(9);
        var enabled = true;
        var client = new ContextTrimmingChatClient(inner, () => enabled);

        await SendAsync(client, messages);
        enabled = false;
        await SendAsync(client, messages);

        Assert.Equal("FFFFFFFFF", Shape(inner.Last));
        Assert.All(messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => Assert.Equal(30_000, ((string)r.Result!).Length));
    }
}

public sealed class BackgroundProcessTests
{
    [Fact]
    public async Task StartReadStop()
    {
        using var dir = new TempDir();
        using var processes = new BackgroundProcesses();
        var tools = BackgroundTools.Create(new Sandbox(dir.Path), processes, new CountingApprover(), dir.NewPermissions()).ToList();

        var started = await tools.CallAsync("start_process", new() { ["command"] = "'ready'; Start-Sleep 30", ["name"] = "sleeper" });
        var output = await tools.CallAsync("process_output", new() { ["id"] = 1, ["wait_seconds"] = 10 });
        var stopped = await tools.CallAsync("stop_process", new() { ["id"] = 1 });
        await Task.Delay(1000);

        Assert.True(started.Contains("Started background program 1"), started);
        Assert.Contains("ready", output);
        Assert.Contains("running", output);
        Assert.Contains("Stopped", stopped);
        Assert.Equal(0, processes.RunningCount);
    }

    [Fact]
    public async Task OnlyFourAtATime()
    {
        using var dir = new TempDir();
        using var processes = new BackgroundProcesses();
        var results = new List<string>();
        for (var i = 0; i < 5; i++)
            results.Add(processes.Start(dir.Path, "Start-Sleep 30", null));

        Assert.StartsWith("Error", results[4]);
        processes.StopAll();
        await Task.Delay(1000);
        Assert.Equal(0, processes.RunningCount);
    }
}

public sealed class ConversationStoreTests
{
    [Fact]
    public void Conversations_RememberTheirModeAndFolder_AndTheirTasks()
    {
        using var dir = new TempDir();
        var store = new ConversationStore(Path.Combine(dir.Path, "history.db"));
        var message = new StoredMessage("user", "hi", null, null, null, null);

        store.Append("chat1", "A chat", [message], null);                         // before Code mode: no mode
        store.Append("code-a", "Code in A", [message], null, "code", @"C:\a");
        store.Append("code-b", "Code in B", [message], null, "code", @"C:\b");
        store.SaveTasks("code-a", "[{\"Content\":\"x\",\"Status\":\"pending\"}]");

        Assert.Equal(["chat1"], store.List(mode: "chat").Select(c => c.Id));
        Assert.Equal(["code-a"], store.List(mode: "code", workspace: @"C:\a").Select(c => c.Id));
        Assert.Equal(2, store.List(mode: "code").Count);
        Assert.Equal(3, store.List().Count);
        Assert.Contains("pending", store.LoadTasks("code-a"));
        Assert.Null(store.LoadTasks("code-b"));
    }

    [Fact]
    public void AnOldDatabase_IsMigratedWithoutLosingConversations()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "history.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, session_state TEXT);
                CREATE TABLE messages (conversation_id TEXT NOT NULL, seq INTEGER NOT NULL, role TEXT NOT NULL, text TEXT NOT NULL, details TEXT, created_at TEXT NOT NULL, PRIMARY KEY (conversation_id, seq));
                INSERT INTO conversations VALUES ('old', 'An old chat', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', NULL);
                """;
            create.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var store = new ConversationStore(path);

        Assert.Equal(["old"], store.List(mode: "chat").Select(c => c.Id));
        Assert.Empty(store.List(mode: "code"));
    }
}

public sealed class CodingInstructionsTests
{
    [Fact]
    public void AFolderWithGitOrAProjectFile_LooksLikeAProject()
    {
        using var plain = new TempDir();
        plain.Write("notes.txt", "x");
        using var git = new TempDir();
        Directory.CreateDirectory(Path.Combine(git.Path, ".git"));
        using var project = new TempDir();
        project.Write("app.csproj", "<Project />");

        Assert.False(CodingInstructions.LooksLikeProject(plain.Path));
        Assert.True(CodingInstructions.LooksLikeProject(git.Path));
        Assert.True(CodingInstructions.LooksLikeProject(project.Path));
    }

    [Fact]
    public void CodePrompt_MentionsOnlyTheToolsThatExist()
    {
        var withAll = CodingPrompt.Build(tasks: true, helpers: true, background: true);
        var bare = CodingPrompt.Build(tasks: false, helpers: false);

        Assert.Contains("update_tasks", withAll);
        Assert.Contains("spawn_agent", withAll);
        Assert.Contains("start_process", withAll);
        Assert.DoesNotContain("update_tasks", bare);
        Assert.DoesNotContain("spawn_agent", bare);
        Assert.DoesNotContain("start_process", bare);
    }
}
