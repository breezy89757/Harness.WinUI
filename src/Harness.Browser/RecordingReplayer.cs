// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Playwright;

namespace Harness.Browser;

/// <summary>
/// Plays a <see cref="Recording"/> again with Playwright and no model: every step is done through the locator that was recorded, and every check is run.
/// A locator that finds nothing, or a step that times out, fails that step and stops the run; the result says which step and which locator.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RecordingReplayer
{
    /// <summary>A secret's value for a replay: the stored one, else the environment variable BROWSER_SECRET_NAME (so a build server can supply it).</summary>
    public static string? DefaultSecretLookup(string name) => BrowserSecrets.Get(name) ?? Environment.GetEnvironmentVariable("BROWSER_SECRET_" + name);

    public static async Task<RunResult> ReplayAsync(
        Recording recording, BrowserOptions? options = null, bool headless = true, Func<string, string?>? secretLookup = null, CancellationToken cancellationToken = default)
    {
        options ??= BrowserOptions.None;
        secretLookup ??= DefaultSecretLookup;
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>();

        RunResult Finish(string status, string summary) =>
            new(recording.Name, "replay-json", status, summary, outcomes, 0, 0, clock.ElapsedMilliseconds, "", started);

        if (!options.Allows(recording.StartUrl))
            return Finish("fail", $"The start address {recording.StartUrl} is not in the list of allowed hosts.");

        BrowserSession session;
        try
        {
            session = await BrowserSession.StartAsync(options, headless).ConfigureAwait(false);
        }
        catch (Exception ex) when (Failures.Is(ex) || ex is IOException)
        {
            return Finish("fail", "Cannot start the browser: " + FirstLine(ex.Message));
        }

        await using (session.ConfigureAwait(false))
        {
            var page = session.Page;
            try
            {
                await page.GotoAsync(recording.StartUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }).ConfigureAwait(false);
            }
            catch (Exception ex) when (Failures.Is(ex))
            {
                return Finish("fail", $"Cannot open {recording.StartUrl}: {FirstLine(ex.Message)}");
            }

            foreach (var step in recording.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stepClock = Stopwatch.StartNew();
                try
                {
                    await RunStepAsync(page, step, secretLookup).ConfigureAwait(false);
                    outcomes.Add(new StepOutcome(step.Index, step.Kind, step.Description, "pass", "", stepClock.ElapsedMilliseconds));
                }
                catch (Exception ex) when (Failures.Is(ex) || ex is InvalidOperationException)
                {
                    var locator = step.Locator is { } l ? $" (locator {Describe(l)})" : "";
                    var reason = FirstLine(ex.Message) + locator;
                    outcomes.Add(new StepOutcome(step.Index, step.Kind, step.Description, "fail", reason, stepClock.ElapsedMilliseconds));
                    foreach (var rest in recording.Steps.Where(s => s.Index > step.Index))
                        outcomes.Add(new StepOutcome(rest.Index, rest.Kind, rest.Description, "skipped", "an earlier step failed", 0));
                    return Finish("fail", $"Step {step.Index} ({step.Kind}) failed: {reason}");
                }
            }
        }
        return Finish("pass", $"All {recording.Steps.Count} steps passed.");
    }

    private static async Task RunStepAsync(IPage page, RecordedStep step, Func<string, string?> secretLookup)
    {
        ILocator Need() => step.Locator is { } info ? Locators.ToLocator(page, info) : throw new InvalidOperationException($"the recorded {step.Kind} step has no locator");

        switch (step.Kind)
        {
            case "goto":
                await page.GotoAsync(step.Url ?? throw new InvalidOperationException("goto without a url"), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }).ConfigureAwait(false);
                break;
            case "click":
                await Need().ClickAsync(new() { Timeout = 8000 }).ConfigureAwait(false);
                break;
            case "fill":
                await Need().FillAsync(BrowserSecrets.Resolve(step.Text ?? "", secretLookup), new() { Timeout = 8000 }).ConfigureAwait(false);
                break;
            case "select":
                await Need().SelectOptionAsync(new SelectOptionValue { Label = step.Text }, new() { Timeout = 8000 }).ConfigureAwait(false);
                break;
            case "check":
                await Need().SetCheckedAsync(!string.Equals(step.Text, "false", StringComparison.OrdinalIgnoreCase), new() { Timeout = 8000 }).ConfigureAwait(false);
                break;
            case "press":
                if (step.Locator is not null)
                    await Need().PressAsync(step.Keys ?? "", new() { Timeout = 8000 }).ConfigureAwait(false);
                else
                    await page.Keyboard.PressAsync(step.Keys ?? "").ConfigureAwait(false);
                break;
            case "assert":
                {
                    var spec = step.Assert ?? throw new InvalidOperationException("assert step without a check");
                    var (ok, actual) = await Asserts.VerifyAsync(page, spec, spec.NeedsElement ? Need() : null, timeoutMs: 6000).ConfigureAwait(false);
                    if (!ok)
                        throw new InvalidOperationException($"the check {spec.Type} \"{spec.Text}\" does not hold: {actual}");
                    break;
                }
            case "done":
                break;
            default:
                throw new InvalidOperationException($"unknown step kind {step.Kind}");
        }
    }

    /// <summary>"role=button name=\"Add\" #2": how a locator reads in a report.</summary>
    internal static string Describe(LocatorInfo l) =>
        l.Strategy + (l.Role is not null ? $"={l.Role}" : "") + $" \"{l.Value}\"" + (l.Nth is { } n ? $" #{n + 1}" : "");

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
