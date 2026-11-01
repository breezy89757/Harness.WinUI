using System.Net;
using System.Text;
using Xunit;

namespace Harness.Browser.Tests;

public class GeneratorTests
{
    [Fact]
    public void The_code_is_taken_from_the_first_fenced_block_or_the_whole_reply()
    {
        Assert.Equal("class A {}\n", CSharpScriptGenerator.ExtractCode("Here:\n```csharp\nclass A {}\n```\nDone."));
        Assert.Equal("class B {}\n", CSharpScriptGenerator.ExtractCode("```cs\nclass B {}\n```"));
        Assert.Equal("class C {}\n", CSharpScriptGenerator.ExtractCode("class C {}"));
    }

    [Fact]
    public void The_prompt_tells_the_model_to_use_only_recorded_locators_expect_and_environment_secrets()
    {
        var prompt = CSharpScriptGenerator.SystemPrompt;
        Assert.Contains("never invent a selector", prompt);
        Assert.Contains("Expect(", prompt);
        Assert.Contains("BROWSER_SECRET_NAME", prompt);
        Assert.Contains("Never write a secret's value", prompt);
    }

    [Fact]
    public void The_generated_project_uses_the_installed_edge_so_nothing_has_to_be_downloaded()
    {
        Assert.Contains("Microsoft.Playwright.MSTest", CSharpScriptGenerator.ProjectFile);
        Assert.Contains("<Channel>msedge</Channel>", CSharpScriptGenerator.RunSettings);
    }

    [Fact]
    public void A_locator_reads_the_way_a_report_should_show_it()
    {
        Assert.Equal("role=button \"Add\" #2", RecordingReplayer.Describe(new LocatorInfo("role", "button", "Add", 1, "")));
        Assert.Equal("label \"Password\"", RecordingReplayer.Describe(new LocatorInfo("label", null, "Password", null, "")));
    }
}

/// <summary>Replays against a small page served from this PC, in the browser that is installed (the test ends quietly when there is none).</summary>
public sealed class ReplayAgainstALocalPageTests : IAsyncLifetime
{
    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Sign in</title></head><body>
        <label for="u">Username</label><input id="u" name="u">
        <label for="p">Password</label><input id="p" name="p" type="password">
        <button id="go" onclick="document.getElementById('msg').textContent = (document.getElementById('p').value === 'pw123') ? 'Welcome ' + document.getElementById('u').value : 'Wrong password'">Sign in</button>
        <p id="msg"></p></body></html>
        """;

    private HttpListener _listener = null!;
    private string _url = "";
    private bool _browserAvailable;

    public async Task InitializeAsync()
    {
        var port = new Random().Next(20000, 40000);
        _listener = new HttpListener();
        _url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_url);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes(Page);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        });

        try
        {
            await using var probe = await BrowserSession.StartAsync(BrowserOptions.None, headless: true);
            _browserAvailable = true;
        }
        catch (Exception ex) when (ex is Microsoft.Playwright.PlaywrightException or IOException)
        {
            _browserAvailable = false;
        }
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        return Task.CompletedTask;
    }

    private Recording Recording(params RecordedStep[] steps) =>
        new("login", _url, "sign in", null, DateTimeOffset.Now, "", steps);

    private RecordedStep Step(int index, string kind, LocatorInfo? locator = null, string? text = null, AssertSpec? assert = null) =>
        new(index, kind, locator, text, null, null, assert, null, _url, _url, "", "", $"{kind} {index}");

    [Fact]
    public async Task A_recording_is_replayed_with_secrets_put_in_and_every_check_run()
    {
        if (!_browserAvailable)
            return;
        var recording = Recording(
            Step(1, "fill", new LocatorInfo("label", null, "Username", null, ""), "qa01"),
            Step(2, "fill", new LocatorInfo("label", null, "Password", null, ""), "{{secret:PASS}}"),
            Step(3, "click", new LocatorInfo("role", "button", "Sign in", null, "")),
            Step(4, "assert", assert: new AssertSpec("text_visible", null, "Welcome qa01")),
            Step(5, "done"));

        var result = await RecordingReplayer.ReplayAsync(recording, secretLookup: name => name == "PASS" ? "pw123" : null);

        Assert.Equal("pass", result.Status);
        Assert.Equal("replay-json", result.Mode);
        Assert.All(result.Steps, s => Assert.Equal("pass", s.Status));
        Assert.DoesNotContain("pw123", result.ToJson());
    }

    [Fact]
    public async Task A_locator_that_finds_nothing_fails_that_step_names_it_and_skips_the_rest()
    {
        if (!_browserAvailable)
            return;
        var recording = Recording(
            Step(1, "fill", new LocatorInfo("label", null, "Username", null, ""), "qa01"),
            Step(2, "click", new LocatorInfo("role", "button", "Log in", null, "")),
            Step(3, "assert", assert: new AssertSpec("text_visible", null, "Welcome qa01")));

        var result = await RecordingReplayer.ReplayAsync(recording, secretLookup: _ => null);

        Assert.Equal("fail", result.Status);
        Assert.Equal(["pass", "fail", "skipped"], result.Steps.Select(s => s.Status));
        Assert.Contains("Step 2", result.Summary);
        Assert.Contains("role=button \"Log in\"", result.Steps[1].Reason);
    }

    [Fact]
    public async Task A_check_that_no_longer_holds_fails_the_step_with_what_was_checked()
    {
        if (!_browserAvailable)
            return;
        var recording = Recording(
            Step(1, "fill", new LocatorInfo("label", null, "Username", null, ""), "qa01"),
            Step(2, "fill", new LocatorInfo("label", null, "Password", null, ""), "wrong"),
            Step(3, "click", new LocatorInfo("role", "button", "Sign in", null, "")),
            Step(4, "assert", assert: new AssertSpec("text_visible", null, "Welcome qa01")));

        var result = await RecordingReplayer.ReplayAsync(recording, secretLookup: _ => null);

        Assert.Equal("fail", result.Status);
        Assert.Contains("text_visible", result.Steps[3].Reason);
        Assert.Contains("Welcome qa01", result.Steps[3].Reason);
    }

    [Fact]
    public async Task A_missing_secret_stops_the_replay_instead_of_typing_a_placeholder()
    {
        if (!_browserAvailable)
            return;
        var recording = Recording(Step(1, "fill", new LocatorInfo("label", null, "Password", null, ""), "{{secret:NOPE}}"));

        var result = await RecordingReplayer.ReplayAsync(recording, secretLookup: _ => null);

        Assert.Equal("fail", result.Status);
        Assert.Contains("NOPE", result.Steps[0].Reason);
    }

    [Fact]
    public async Task A_start_address_outside_the_allowed_hosts_is_refused()
    {
        var result = await RecordingReplayer.ReplayAsync(Recording(Step(1, "done")), new BrowserOptions(["example.com"], null), secretLookup: _ => null);
        Assert.Equal("fail", result.Status);
        Assert.Contains("allowed hosts", result.Summary);
    }
}
