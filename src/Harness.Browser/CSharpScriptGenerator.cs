// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Config;
using Microsoft.Extensions.AI;

namespace Harness.Browser;

/// <param name="CompiledFirstTry">The code the model wrote built without a change.</param>
/// <param name="Compiled">It builds now (after up to two fixes).</param>
public sealed record GeneratedScript(string ProjectFolder, string FilePath, bool CompiledFirstTry, bool Compiled, int FixAttempts, string Errors, long InputTokens, long OutputTokens);

/// <summary>
/// Turns a <see cref="Recording"/> into a Playwright test class (MSTest) with a model, using the recorded locators and Expect assertions only, then builds it at
/// once; when it does not build, the compiler's errors go back to the model, at most twice. The class is meant to be run without any model afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CSharpScriptGenerator(IChatClient client)
{
    public const int MaxFixes = 2;

    /// <summary>Where generated test projects go unless told otherwise: Generated, in the app's data folder.</summary>
    public static string DefaultFolder => AppPaths.Combine("Generated");

    internal const string SystemPrompt = """
        You turn a recorded browser test into one C# test class for Microsoft.Playwright.MSTest. Reply with ONE C# file in a single ```csharp block and nothing else.

        File layout:
        - usings: System, System.Text.RegularExpressions, Microsoft.Playwright, Microsoft.Playwright.MSTest, Microsoft.VisualStudio.TestTools.UnitTesting
        - namespace GeneratedTests; [TestClass] public class <ClassName> : PageTest, with exactly one [TestMethod] public async Task Run().
        - First line of the method: await Page.GotoAsync("<StartUrl>");
        - Then every recorded step, in order, each preceded by a comment: // Step N: <description>.

        Locators: use ONLY the locator recorded for the step; never invent a selector. Translate by Strategy:
        - role        -> Page.GetByRole(AriaRole.<Role in PascalCase, e.g. Button, Link, Textbox, Checkbox, Combobox>, new() { Name = "<Value>", Exact = true })
        - label       -> Page.GetByLabel("<Value>", new() { Exact = true })
        - placeholder -> Page.GetByPlaceholder("<Value>", new() { Exact = true })
        - testid      -> Page.GetByTestId("<Value>")
        - text        -> Page.GetByText("<Value>", new() { Exact = true })
        - css         -> Page.Locator("<Value>")
        - xpath       -> Page.Locator("xpath=<Value>")
        If Nth is present, append .Nth(<Nth>).

        Actions by Kind:
        - goto   -> await Page.GotoAsync("<Url>");
        - click  -> await <locator>.ClickAsync();
        - fill   -> await <locator>.FillAsync(<text>);
        - select -> await <locator>.SelectOptionAsync(new SelectOptionValue { Label = "<Text>" });
        - check  -> await <locator>.SetCheckedAsync(true or false, from Text);
        - press  -> await <locator>.PressAsync("<Keys>"); or, with no locator, await Page.Keyboard.PressAsync("<Keys>");
        - assert -> use Expect, by Assert.Type:
            text_visible    -> await Expect(Page.Locator("body")).ToContainTextAsync("<Text>");
            url_contains    -> await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape("<Text>")));
            title_contains  -> await Expect(Page).ToHaveTitleAsync(new Regex(Regex.Escape("<Text>")));
            element_visible -> await Expect(<locator>).ToBeVisibleAsync();
            element_text    -> await Expect(<locator>).ToContainTextAsync("<Text>");
            element_value   -> await Expect(<locator>).ToHaveValueAsync("<Text>");
        - done   -> a comment only.

        Every Expect(...) assertion takes new() { Timeout = 15000 } as its last argument (a page can need several seconds to show a result; the default is 5 s).

        Secrets: a text that contains {{secret:NAME}} must be built from the environment variable BROWSER_SECRET_NAME, read with
        Environment.GetEnvironmentVariable("BROWSER_SECRET_NAME") ?? throw new InvalidOperationException("Set BROWSER_SECRET_NAME"). Never write a secret's value.

        Escape C# string literals correctly (quotes, backslashes, newlines). No Thread.Sleep or Task.Delay, no extra steps, no helper libraries, no explanations.
        """;

    public async Task<GeneratedScript> GenerateAsync(Recording recording, string? folder = null, string? chosenName = null, CancellationToken cancellationToken = default)
    {
        folder ??= DefaultFolder;
        var name = chosenName ?? $"{Naming.Slug(recording.Name)}-{recording.RecordedAt:yyyyMMdd-HHmmss}";
        var project = Path.Combine(folder, name);
        Directory.CreateDirectory(project);
        var className = "GeneratedTest_" + recording.RecordedAt.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var file = Path.Combine(project, className + ".cs");
        await File.WriteAllTextAsync(Path.Combine(project, "GeneratedTests.csproj"), ProjectFile, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(project, "playwright.runsettings"), RunSettings, cancellationToken).ConfigureAwait(false);

        long inTokens = 0, outTokens = 0;
        var user = $"ClassName: {className}\n\nRecording (JSON):\n{recording.ToJson()}";
        var code = await AskForCodeAsync(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, user)], cancellationToken, t => { inTokens += t.In; outTokens += t.Out; }).ConfigureAwait(false);

        var fixes = 0;
        var firstTry = false;
        var errors = "";
        while (true)
        {
            await File.WriteAllTextAsync(file, code, cancellationToken).ConfigureAwait(false);
            (var built, errors) = await BuildAsync(project, cancellationToken).ConfigureAwait(false);
            if (fixes == 0)
                firstTry = built;
            if (built)
                return new GeneratedScript(project, file, firstTry, true, fixes, "", inTokens, outTokens);
            if (fixes >= MaxFixes)
                return new GeneratedScript(project, file, firstTry, false, fixes, errors, inTokens, outTokens);

            fixes++;
            code = await AskForCodeAsync(
                [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, user),
                 new ChatMessage(ChatRole.Assistant, "```csharp\n" + code + "\n```"),
                 new ChatMessage(ChatRole.User, $"It does not build. Compiler errors:\n{errors}\n\nFix them and reply with the whole file again in one ```csharp block, changing nothing else.")],
                cancellationToken, t => { inTokens += t.In; outTokens += t.Out; }).ConfigureAwait(false);
        }
    }

    private async Task<string> AskForCodeAsync(IList<ChatMessage> messages, CancellationToken cancellationToken, Action<(long In, long Out)> usage)
    {
        var response = await client.GetResponseAsync(messages, null, cancellationToken).ConfigureAwait(false);
        usage((response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0));
        return ExtractCode(response.Text);
    }

    /// <summary>The code of the first fenced block; the whole text when there is no fence.</summary>
    internal static string ExtractCode(string reply)
    {
        var match = CodeFence().Match(reply);
        return (match.Success ? match.Groups[1].Value : reply).Trim() + "\n";
    }

    [GeneratedRegex(@"```(?:csharp|cs|c#)?\s*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();

    internal const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="Microsoft.Playwright.MSTest" Version="1.64.1" />
            <!-- Playwright.MSTest pulls in MSTest 2.2.7, whose adapter finds no tests under the current test platform: name a current one. -->
            <PackageReference Include="MSTest.TestAdapter" Version="3.9.3" />
            <PackageReference Include="MSTest.TestFramework" Version="3.9.3" />
          </ItemGroup>

        </Project>
        """;

    // Uses the Edge that is already on the PC (no browser download); change Channel (or remove it, after "playwright install") to use another.
    internal const string RunSettings = """
        <?xml version="1.0" encoding="utf-8"?>
        <RunSettings>
          <Playwright>
            <BrowserName>chromium</BrowserName>
            <LaunchOptions>
              <Channel>msedge</Channel>
              <Headless>true</Headless>
            </LaunchOptions>
          </Playwright>
        </RunSettings>
        """;

    /// <summary>Builds the project; the compiler errors (at most 15 lines, without folder names) when it fails.</summary>
    public static async Task<(bool Built, string Errors)> BuildAsync(string projectFolder, CancellationToken cancellationToken)
    {
        var (exit, output) = await RunDotnetAsync(projectFolder, "build -nologo -v q", new Dictionary<string, string>(), TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (exit == 0)
            return (true, "");
        var errors = output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(": error ", StringComparison.Ordinal) || l.Contains("error NU", StringComparison.Ordinal))
            .Select(l => l.Replace(projectFolder + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)).Distinct().Take(15);
        var text = string.Join('\n', errors);
        return (false, text.Length > 0 ? text : string.Join('\n', output.Split('\n').TakeLast(10)));
    }

    /// <summary>Runs the generated test (no model involved) and reports it as a run result of mode replay-cs.</summary>
    public static async Task<RunResult> RunTestAsync(GeneratedScript script, Recording recording, Func<string, string?>? secretLookup = null, CancellationToken cancellationToken = default)
    {
        secretLookup ??= RecordingReplayer.DefaultSecretLookup;
        var started = DateTimeOffset.Now;
        var env = new Dictionary<string, string>();
        foreach (var secret in recording.Steps.SelectMany(s => BrowserSecrets.NamesIn(s.Text)).Distinct())
        {
            if (secretLookup(secret) is { } value)
                env["BROWSER_SECRET_" + secret] = value;
        }

        var clock = Stopwatch.StartNew();
        // exit code 0 is not enough: a run that found no test also ends with 0. The result file says how many tests really ran.
        var results = Path.Combine(script.ProjectFolder, "TestResults");
        if (Directory.Exists(results))
            Directory.Delete(results, recursive: true);
        var (exit, output) = await RunDotnetAsync(script.ProjectFolder, "test --settings playwright.runsettings -nologo --logger \"trx;LogFileName=result.trx\" --results-directory TestResults", env, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var counts = ReadCounters(Path.Combine(results, "result.trx"));
        var passed = exit == 0 && counts is { Executed: > 0, Failed: 0, Passed: > 0 };
        var reason = passed ? "" : counts is { Executed: 0 } or null && exit == 0
            ? "No test was run (the test adapter found none)."
            : string.Join(' ', output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).SkipWhile(l => !l.Contains("Error Message", StringComparison.OrdinalIgnoreCase) && !l.Contains("錯誤訊息", StringComparison.Ordinal)).Take(4));
        if (!passed && reason.Length == 0)
            reason = string.Join(' ', output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(4));
        var step = new StepOutcome(1, "dotnet test", Path.GetFileName(script.FilePath), passed ? "pass" : "fail", BrowserSecrets.Mask(reason, env.ToDictionary(e => e.Key["BROWSER_SECRET_".Length..], e => e.Value)), clock.ElapsedMilliseconds);
        return new RunResult(recording.Name, "replay-cs", passed ? "pass" : "fail", passed ? "The generated test passed." : "The generated test failed: " + step.Reason, [step], 0, 0, clock.ElapsedMilliseconds, "", started);
    }

    /// <summary>The counters of a .trx result file (tests executed, passed, failed); null when the file is missing or unreadable.</summary>
    internal static (int Executed, int Passed, int Failed)? ReadCounters(string trxPath)
    {
        try
        {
            var match = TrxCounters().Match(File.ReadAllText(trxPath));
            return match.Success ? (int.Parse(match.Groups["executed"].Value), int.Parse(match.Groups["passed"].Value), int.Parse(match.Groups["failed"].Value)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<Counters\b[^>]*?\bexecuted=""(?<executed>\d+)""[^>]*?\bpassed=""(?<passed>\d+)""[^>]*?\bfailed=""(?<failed>\d+)""", RegexOptions.Singleline)]
    private static partial Regex TrxCounters();

    private static async Task<(int Exit, string Output)> RunDotnetAsync(string folder, string arguments, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        info.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var (key, value) in env)
            info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start dotnet.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return (-1, output + "\nTimed out.");
        }
        process.WaitForExit(); // lets the output readers finish
        lock (output)
            return (process.ExitCode, output.ToString());
    }
}
