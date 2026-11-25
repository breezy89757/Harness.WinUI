// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Observability;

namespace Harness.Core.Tests;

public class TraceQueryTests
{
    [Fact]
    public void Parse_splits_words_quotes_and_exclusions()
    {
        var query = TraceQuery.Parse("""app:鼓勵 -timeout "two words" -tool:"read file" model:luna is:error""");
        Assert.Equal(
        [
            new TraceTerm("app", "鼓勵", false),
            new TraceTerm("", "timeout", true),
            new TraceTerm("", "two words", false),
            new TraceTerm("tool", "read file", true),
            new TraceTerm("model", "luna", false),
            new TraceTerm("is", "error", false),
        ], query.Terms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tool: model:")]
    [InlineData("-")]
    public void Parse_of_nothing_is_empty(string text) => Assert.True(TraceQuery.Parse(text).IsEmpty || TraceQuery.Parse(text).Terms.All(t => t.Value == "-"));

    [Fact]
    public void Parse_keeps_unknown_fields_and_values_as_text()
    {
        Assert.Equal([new TraceTerm("", "foo:bar", false)], TraceQuery.Parse("foo:bar").Terms);
        Assert.Equal([new TraceTerm("", "is:weird", false)], TraceQuery.Parse("is:weird").Terms);
        Assert.Equal([new TraceTerm("app", "", false)], TraceQuery.Parse("app:").Terms);
    }

    private static SpanRecord Turn(string id, string? title, string? conversation, string name = "turn", bool failed = false) =>
        new(id, id, null, "turn", name, DateTimeOffset.UtcNow, 100, failed, null, conversation, null, null, null, null, null, null, null, null,
            null, null, new Dictionary<string, string>(), title is null ? null : new Dictionary<string, string> { [Telemetry.TagUserMessage] = title });

    private static SpanRecord Chat(string trace, string model, string? tool = null) =>
        new("c" + trace, trace, trace, tool is null ? "chat" : "execute_tool", "x", DateTimeOffset.UtcNow, 10, false, null, null, model, 5, 5, 0, null,
            null, null, null, tool, null, new Dictionary<string, string>());

    private static TraceStore Fill(TempDir dir)
    {
        var store = new TraceStore(Path.Combine(dir.Path, "trace.db"));
        store.Insert(Turn("t1", "做一個番茄鐘", "conv-1"));
        store.Insert(Chat("t1", "gpt-luna"));
        store.Insert(Turn("t2", "[今日一句鼓勵] 請給我一句", "miniapp:aaa", "app:今日一句鼓勵"));
        store.Insert(Chat("t2", "gpt-luna"));
        store.Insert(Turn("t3", "[番茄鐘助手] timeout test", "miniapp:bbb", "app:番茄鐘助手", failed: true));
        store.Insert(Chat("t3", "other-model"));
        store.Insert(Turn("t4", "[今日一句鼓勵] 再一句", "miniapp:aaa", "app:今日一句鼓勵"));
        store.Insert(Turn("t5", null, "miniapp:ccc", "app:無內容的App")); // content was not recorded
        store.Insert(Turn("t6", "查檔案", "conv-2"));
        store.Insert(Chat("t6", "gpt-luna", tool: "read_file"));
        return store;
    }

    private static string[] Ids(TraceStore store, string? search, bool failedOnly = false) =>
        [.. store.Turns(DateTimeOffset.UtcNow.AddDays(-1), search, failedOnly).Select(t => t.TraceId).Order()];

    [Fact]
    public void Plain_words_still_search_message_tool_and_model()
    {
        using var dir = new TempDir();
        var store = Fill(dir);
        Assert.Equal(["t1"], Ids(store, "番茄鐘 做一個"));
        Assert.Equal(["t6"], Ids(store, "read_file"));
        Assert.Equal(["t3"], Ids(store, "other-model"));
        Assert.Equal(["t1", "t2", "t6"], Ids(store, "luna"));
    }

    [Fact]
    public void App_filter_picks_a_pinned_app_by_name_or_all()
    {
        using var dir = new TempDir();
        var store = Fill(dir);
        Assert.Equal(["t2", "t4"], Ids(store, "app:鼓勵"));
        Assert.Equal(["t3"], Ids(store, "app:番茄"));
        Assert.Equal(["t5"], Ids(store, "app:無內容")); // by the turn's name, with no message kept
        Assert.Equal(["t2", "t3", "t4", "t5"], Ids(store, "app:"));
        Assert.Equal(["t2", "t3", "t4", "t5"], Ids(store, "is:app"));
        Assert.Equal(["t1", "t6"], Ids(store, "is:chat"));
    }

    [Fact]
    public void Exclusions_remove_what_matches()
    {
        using var dir = new TempDir();
        var store = Fill(dir);
        Assert.Equal(["t2", "t4", "t5"], Ids(store, "is:app -app:番茄"));
        Assert.Equal(["t1", "t2", "t4", "t5", "t6"], Ids(store, "-timeout"));
        Assert.Equal(["t1", "t6"], Ids(store, "luna -app:"));
        Assert.Equal(["t1", "t2", "t4", "t5", "t6"], Ids(store, "-model:other"));
        Assert.Equal(["t1", "t2", "t3", "t4", "t5"], Ids(store, "-tool:read_file"));
    }

    [Fact]
    public void Conditions_combine_and_is_error_matches_the_checkbox()
    {
        using var dir = new TempDir();
        var store = Fill(dir);
        Assert.Equal(["t2"], Ids(store, "app:鼓勵 model:luna 請給我"));
        Assert.Equal(["t3"], Ids(store, "is:error"));
        Assert.Equal(Ids(store, "is:error"), Ids(store, null, failedOnly: true));
        Assert.Equal(["t2", "t4", "t5"], Ids(store, "is:app -is:error"));
    }

    [Fact]
    public void Wildcard_characters_in_the_search_are_taken_literally()
    {
        using var dir = new TempDir();
        var store = new TraceStore(Path.Combine(dir.Path, "trace.db"));
        store.Insert(Turn("a", "100% done", "c"));
        store.Insert(Turn("b", "100 done", "c"));
        Assert.Equal(["a"], Ids(store, "100%"));
        Assert.Equal([], Ids(store, "a_b"));
    }

    [Fact]
    public void AppNames_lists_apps_with_and_without_recorded_messages()
    {
        using var dir = new TempDir();
        var store = Fill(dir);
        Assert.Equal(["今日一句鼓勵", "無內容的App", "番茄鐘助手"], store.AppNames(DateTimeOffset.UtcNow.AddDays(-1)).Order().ToArray());
    }
}
