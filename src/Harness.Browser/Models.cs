// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Core.Agent;

namespace Harness.Browser;

/// <summary>What the model asks for. <c>Id</c> is the number of an element in the list it was shown; the program turns it into a locator.</summary>
internal sealed record BrowserMove(
    string Kind, int? Id, string? Text, string? Url, string? Keys, string? Direction, AssertSpec? Assert, string? Result, string Reason)
{
    public static readonly string[] Kinds = ["goto", "click", "fill", "select", "check", "press", "scroll", "wait", "assert", "done", "blocked", "ask_user"];

    public static BrowserMove Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new JsonException("no JSON object in the reply");
        using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
        var r = doc.RootElement;
        string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        var kind = Str(r, "kind")?.Trim().ToLowerInvariant() ?? throw new JsonException("missing kind");
        if (!Kinds.Contains(kind))
            throw new JsonException($"unknown kind \"{kind}\"; use one of: {string.Join(", ", Kinds)}");

        AssertSpec? assert = null;
        if (r.TryGetProperty("assert", out var a) && a.ValueKind == JsonValueKind.Object)
            assert = new AssertSpec(Str(a, "type") ?? "", Num(a, "id"), Str(a, "text") ?? "");

        return new BrowserMove(kind, Num(r, "id"), Str(r, "text"), Str(r, "url"), Str(r, "keys"), Str(r, "direction"), assert, Str(r, "result"), Str(r, "reason") ?? "");
    }
}

/// <summary>
/// A check the model asks for, and later a replay runs without it. Type: text_visible, url_contains, title_contains,
/// element_visible, element_text, element_value (the last three name an element by <c>Id</c> while exploring; a recording keeps its locator).
/// </summary>
public sealed record AssertSpec(string Type, int? Id, string Text)
{
    public static readonly string[] Types = ["text_visible", "url_contains", "title_contains", "element_visible", "element_text", "element_value"];

    public bool NeedsElement => Type is "element_visible" or "element_text" or "element_value";
}

/// <summary>How to find an element again, and why that way was chosen.</summary>
/// <param name="Strategy">role, label, placeholder, testid, text, css or xpath (the order is the order of preference).</param>
/// <param name="Role">For role: the ARIA role (button, link, textbox …).</param>
/// <param name="Value">The accessible name (role), label, placeholder, test id, text, or the selector.</param>
/// <param name="Nth">Set when the same locator finds several elements; the position among them (0-based).</param>
/// <param name="Reason">Why this strategy (for example "role + name is unique on the page").</param>
public sealed record LocatorInfo(string Strategy, string? Role, string Value, int? Nth, string Reason);

/// <summary>One step of a recording: something that worked while exploring, with what a replay needs to do it again.</summary>
/// <param name="ExpectedResult">For a check (assert) or the final done: what the model said should be true, in its own words.</param>
/// <param name="ScreenBefore">A short description of the page before the step (title and how many things could be acted on), and after it in <paramref name="ScreenAfter"/>.</param>
public sealed record RecordedStep(
    int Index, string Kind, LocatorInfo? Locator, string? Text, string? Url, string? Keys, AssertSpec? Assert,
    string? ExpectedResult, string UrlBefore, string UrlAfter, string ScreenBefore, string ScreenAfter, string Description);

/// <summary>A test case as it was explored: what a replay (JSON or generated C#) is made from. Secrets appear only as <c>{{secret:NAME}}</c>.</summary>
public sealed record Recording(
    string Name, string StartUrl, string Task, string? Expected, DateTimeOffset RecordedAt, string Model, IReadOnlyList<RecordedStep> Steps)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static Recording? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Recording>(json, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>pass / fail / blocked (a person had to take over) / skipped (an earlier step stopped the run).</summary>
public sealed record StepOutcome(int Index, string Kind, string Description, string Status, string Reason, long DurationMs);

/// <param name="Mode">explore, replay-json or replay-cs.</param>
public sealed record RunResult(
    string Name, string Mode, string Status, string Summary, IReadOnlyList<StepOutcome> Steps,
    long InputTokens, long OutputTokens, long DurationMs, string Model, DateTimeOffset StartedAt)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public string ToMarkdown()
    {
        var lines = new List<string>
        {
            $"# {Name}",
            "",
            $"- 模式：{Mode}　結果：**{Status}**　耗時：{DurationMs / 1000.0:F1} 秒",
            $"- 模型：{(Model.Length > 0 ? Model : "—")}　token：輸入 {InputTokens:N0} / 輸出 {OutputTokens:N0}",
            $"- 開始：{StartedAt:yyyy-MM-dd HH:mm:ss}",
            "",
            Summary,
            "",
            "| # | 動作 | 結果 | 說明 | 耗時 |",
            "|---:|---|---|---|---:|",
        };
        foreach (var s in Steps)
            lines.Add($"| {s.Index} | {Cell(s.Description)} | {s.Status} | {Cell(s.Reason)} | {s.DurationMs} ms |");
        return string.Join('\n', lines) + "\n";

        static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}

/// <summary>How an exploring run ended.</summary>
/// <param name="Finished">The agent said it was done (whether the check passed is in <paramref name="Passed"/>).</param>
/// <param name="NeedsUser">It stopped to ask the person something.</param>
internal sealed record BrowserOutcome(
    bool Finished, bool Passed, bool NeedsUser, string Report, int Actions, UsageReported Usage, Recording? Recording, IReadOnlyList<StepOutcome> Steps);
