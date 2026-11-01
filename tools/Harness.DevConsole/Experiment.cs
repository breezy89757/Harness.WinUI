// Harness.WinUI — Licensed under the MIT License.
//
// The browser agent experiment (--experiment): a few cases on public test sites, each explored several times; every recording that comes out is replayed
// from JSON, turned into a C# test (and, for comparison, a Python Playwright script) and run again. Everything goes to one folder; the numbers to results.json.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harness.Browser;
using Harness.Core.Config;
using Harness.Core.Providers;
using Microsoft.Extensions.AI;

namespace Harness.DevConsole;

internal sealed record ExperimentCase(string Id, string Level, string StartUrl, string Task, string Expected);

internal sealed record ExploreRow(string Case, int Run, string Status, int Actions, long InputTokens, long OutputTokens, long Ms, string Report, string? Recording, IReadOnlyList<string> Locators);
internal sealed record ReplayRow(string Case, int Run, int Repeat, string Status, long Ms, string Reason);
internal sealed record CSharpRow(string Case, int Run, bool FirstTry, bool Compiled, int Fixes, long InputTokens, long OutputTokens, long GenerateMs, IReadOnlyList<string> Reruns, IReadOnlyList<long> RerunMs, string Errors);
internal sealed record PythonRow(string Case, int Run, bool ParsedFirstTry, string FirstRun, long InputTokens, long OutputTokens, long GenerateMs, long RunMs, string Reason);

internal static class Experiment
{
    public static readonly IReadOnlyList<ExperimentCase> Cases =
    [
        new("simple", "simple", "https://demo.playwright.dev/todomvc/",
            "新增一筆待辦事項「買牛奶」。",
            "清單裡出現「買牛奶」，剩餘項目計數顯示 1 item left。"),
        new("medium", "medium", "https://demo.playwright.dev/todomvc/",
            "依序新增三筆待辦事項：「買牛奶」「寄信」「寫報告」；把「寄信」勾選為完成；切換到 Active 篩選；確認列表只剩「買牛奶」和「寫報告」兩筆、沒有「寄信」，並確認剩餘項目計數是 2 items left。",
            "Active 篩選下只看到「買牛奶」和「寫報告」，看不到「寄信」，計數顯示 2 items left。"),
        new("hard", "hard", "https://the-internet.herokuapp.com/dynamic_loading/2",
            "按下 Start 按鈕，等待內容載入（載入需要幾秒，載入前那段文字不存在於頁面），確認出現「Hello World!」。",
            "載入完成後頁面上顯示「Hello World!」。"),
    ];

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task RunAsync(ResolvedProvider provider, string outFolder, int runs, IReadOnlySet<string> only, bool withPython, int reruns, string? from = null)
    {
        Directory.CreateDirectory(outFolder);
        var client = ChatClientFactory.Create(provider);
        var model = client.GetService<ChatClientMetadata>()?.DefaultModelId ?? provider.Model;
        var options = BrowserOptions.None;
        var explore = new List<ExploreRow>();
        var replay = new List<ReplayRow>();
        var csharp = new List<CSharpRow>();
        var python = new List<PythonRow>();
        var recordingsFolder = Path.Combine(outFolder, "recordings");
        var runsFolder = Path.Combine(outFolder, "runs");
        var generatedFolder = Path.Combine(outFolder, "generated");
        var pythonFolder = Path.Combine(outFolder, "python");

        void Save() => File.WriteAllText(Path.Combine(outFolder, "results.json"),
            JsonSerializer.Serialize(new { model, runs, reruns, explore, replay, csharp, python }, s_json));

        var cases = Cases.Where(c => only.Count == 0 || only.Contains(c.Id)).ToList();

        // --from: keep the exploring and the JSON replays of an earlier run, and do the C# and Python stages again.
        if (from is not null)
        {
            using var earlier = JsonDocument.Parse(File.ReadAllText(Path.Combine(from, "results.json")));
            explore.AddRange(earlier.RootElement.GetProperty("explore").Deserialize<List<ExploreRow>>()!);
            replay.AddRange(earlier.RootElement.GetProperty("replay").Deserialize<List<ReplayRow>>()!);
            Save();
            cases = [];
        }

        // 1. Explore, runs times per case.
        foreach (var c in cases)
        {
            for (var run = 1; run <= runs; run++)
            {
                var clock = Stopwatch.StartNew();
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                BrowserOutcome? outcome = null;
                string status;
                var report = "";
                try
                {
                    var agent = new BrowserAgent(client, options, model, headless: true);
                    outcome = await agent.RunAsync(c.StartUrl, c.Task, c.Expected, limit.Token);
                    status = outcome.Finished ? (outcome.Passed ? "pass" : "fail") : outcome.NeedsUser ? "paused" : "error";
                    report = outcome.Report;
                }
                catch (OperationCanceledException)
                {
                    status = "timeout";
                    report = "8 minute limit";
                }
                catch (Exception ex)
                {
                    status = "error";
                    report = ex.GetType().Name + ": " + ex.Message;
                }

                string? recordingPath = null;
                IReadOnlyList<string> locators = [];
                if (outcome is { Finished: true, Passed: true, Recording: { } recording })
                {
                    recordingPath = RecordingStore.Save(recording, recordingsFolder);
                    // keep the run's own file name unique: Save names by case and time (one second apart at least)
                    locators = recording.Steps.Select(s => s.Locator is { } l ? RecordingReplayer.Describe(l) : s.Kind).ToList();
                }
                if (outcome is not null)
                    RecordingStore.SaveRun(new RunResult($"{c.Id}-{run}", "explore", status, report, outcome.Steps,
                        outcome.Usage.InputTokens ?? 0, outcome.Usage.OutputTokens ?? 0, clock.ElapsedMilliseconds, model, DateTimeOffset.Now.AddMilliseconds(-clock.ElapsedMilliseconds)), runsFolder);

                var row = new ExploreRow(c.Id, run, status, outcome?.Actions ?? 0, outcome?.Usage.InputTokens ?? 0, outcome?.Usage.OutputTokens ?? 0, clock.ElapsedMilliseconds, Trim(report), recordingPath, locators);
                explore.Add(row);
                Console.WriteLine($"[explore] {c.Id} #{run}: {status}, {row.Actions} actions, {row.InputTokens}/{row.OutputTokens} tokens, {row.Ms / 1000.0:F1}s");
                Save();
            }
        }

        // 2. JSON replay: every recording, several times, no model.
        foreach (var row in explore.Where(r => r.Recording is not null && from is null))
        {
            var recording = RecordingStore.Load(row.Recording!)!;
            for (var repeat = 1; repeat <= reruns; repeat++)
            {
                var result = await RecordingReplayer.ReplayAsync(recording, options, headless: true);
                var failed = result.Steps.FirstOrDefault(s => s.Status != "pass");
                replay.Add(new ReplayRow(row.Case, row.Run, repeat, result.Status, result.DurationMs, failed is null ? "" : Trim($"step {failed.Index} {failed.Description}: {failed.Reason}")));
                Console.WriteLine($"[replay-json] {row.Case} #{row.Run} x{repeat}: {result.Status}, {result.DurationMs} ms");
                Save();
            }
        }

        // 3. C#: generate (build, up to 2 fixes), then re-run the built test.
        var csharpGenerator = new CSharpScriptGenerator(client);
        foreach (var row in explore.Where(r => r.Recording is not null))
        {
            var recording = RecordingStore.Load(row.Recording!)!;
            var clock = Stopwatch.StartNew();
            var script = await csharpGenerator.GenerateAsync(recording, generatedFolder, $"{row.Case}-{row.Run}");
            var generateMs = clock.ElapsedMilliseconds;
            var results = new List<string>();
            var times = new List<long>();
            if (script.Compiled)
            {
                for (var repeat = 1; repeat <= reruns; repeat++)
                {
                    var run = await CSharpScriptGenerator.RunTestAsync(script, recording);
                    results.Add(run.Status);
                    times.Add(run.DurationMs);
                    Console.WriteLine($"[replay-cs] {row.Case} #{row.Run} x{repeat}: {run.Status}, {run.DurationMs} ms {(run.Status == "pass" ? "" : Trim(run.Summary))}");
                }
            }
            csharp.Add(new CSharpRow(row.Case, row.Run, script.CompiledFirstTry, script.Compiled, script.FixAttempts, script.InputTokens, script.OutputTokens, generateMs, results, times, Trim(script.Errors)));
            Console.WriteLine($"[generate-cs] {row.Case} #{row.Run}: first try {script.CompiledFirstTry}, builds {script.Compiled}, fixes {script.FixAttempts}, {script.InputTokens}/{script.OutputTokens} tokens");
            Save();
        }

        // 4. Python Playwright, for comparison: the same recordings, one generation each, one run each, no fixing.
        if (withPython)
        {
            foreach (var row in explore.Where(r => r.Recording is not null))
            {
                var recording = RecordingStore.Load(row.Recording!)!;
                var python1 = await PythonScript.GenerateAndRunAsync(client, recording, Path.Combine(pythonFolder, $"{row.Case}-{row.Run}.py"));
                python.Add(new PythonRow(row.Case, row.Run, python1.Parsed, python1.RunStatus, python1.InputTokens, python1.OutputTokens, python1.GenerateMs, python1.RunMs, Trim(python1.Reason)));
                Console.WriteLine($"[python] {row.Case} #{row.Run}: parsed {python1.Parsed}, run {python1.RunStatus}, {python1.RunMs} ms {Trim(python1.Reason)}");
                Save();
            }
        }

        Console.WriteLine($"Done. Results: {Path.Combine(outFolder, "results.json")}");
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>Python Playwright (sync API, plain script) from a recording, for the comparison with the C# test.</summary>
internal static class PythonScript
{
    private const string SystemPrompt = """
        You turn a recorded browser test into ONE Python script that uses the Playwright sync API (from playwright.sync_api import sync_playwright, expect). Reply with ONE Python file in a single ```python block and nothing else.

        Layout: with sync_playwright() as p: browser = p.chromium.launch(channel="msedge", headless=True); page = browser.new_page(); page.goto("<StartUrl>"); then every recorded step in order, each preceded by a comment "# Step N: <description>"; finally browser.close() and print("PASS").
        Locators: use ONLY the locator recorded for the step; never invent a selector. By Strategy:
        - role        -> page.get_by_role("<role>", name="<Value>", exact=True)
        - label       -> page.get_by_label("<Value>", exact=True)
        - placeholder -> page.get_by_placeholder("<Value>", exact=True)
        - testid      -> page.get_by_test_id("<Value>")
        - text        -> page.get_by_text("<Value>", exact=True)
        - css         -> page.locator("<Value>")
        - xpath       -> page.locator("xpath=<Value>")
        If Nth is present, append .nth(<Nth>).
        Actions by Kind: goto -> page.goto(url); click -> .click(); fill -> .fill(text); select -> .select_option(label=text); check -> .set_checked(True/False from Text); press -> .press(keys) or page.keyboard.press(keys) with no locator.
        Every expect(...) call's matcher takes timeout=15000 (a page can need several seconds to show a result; the default is 5 s). Asserts (use expect, never a bare assert): text_visible -> expect(page.locator("body")).to_contain_text(text); url_contains -> expect(page).to_have_url(re.compile(re.escape(text))); title_contains -> expect(page).to_have_title(re.compile(re.escape(text))); element_visible -> expect(locator).to_be_visible(); element_text -> expect(locator).to_contain_text(text); element_value -> expect(locator).to_have_value(text). done -> a comment only.
        Secrets ({{secret:NAME}}) come from os.environ["BROWSER_SECRET_NAME"]; never write a value. No sleeps, no extra steps, no explanations.
        """;

    public sealed record Outcome(bool Parsed, string RunStatus, long InputTokens, long OutputTokens, long GenerateMs, long RunMs, string Reason);

    public static async Task<Outcome> GenerateAndRunAsync(IChatClient client, Recording recording, string file)
    {
        var clock = Stopwatch.StartNew();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, "Recording (JSON):\n" + recording.ToJson())]);
        var generateMs = clock.ElapsedMilliseconds;
        var code = ExtractCode(response.Text);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, code, new UTF8Encoding(false));
        var inT = response.Usage?.InputTokenCount ?? 0;
        var outT = response.Usage?.OutputTokenCount ?? 0;

        var (compileExit, compileOut) = await RunAsync("-m py_compile \"" + file + "\"", Path.GetDirectoryName(file)!);
        if (compileExit != 0)
            return new Outcome(false, "not-run", inT, outT, generateMs, 0, compileOut);

        clock.Restart();
        var (exit, output) = await RunAsync("\"" + file + "\"", Path.GetDirectoryName(file)!);
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var passed = exit == 0 && lines.Contains("PASS");
        return new Outcome(true, passed ? "pass" : "fail", inT, outT, generateMs, clock.ElapsedMilliseconds, passed ? "" : string.Join(' ', lines.TakeLast(4)));
    }

    private static string ExtractCode(string reply)
    {
        var match = System.Text.RegularExpressions.Regex.Match(reply, @"```(?:python|py)?\s*\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (match.Success ? match.Groups[1].Value : reply).Trim() + "\n";
    }

    private static async Task<(int Exit, string Output)> RunAsync(string arguments, string folder)
    {
        var info = new ProcessStartInfo("python", "-X utf8 " + arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "Timed out.");
        }
        process.WaitForExit();
        lock (output)
            return (process.ExitCode, output.ToString());
    }
}
