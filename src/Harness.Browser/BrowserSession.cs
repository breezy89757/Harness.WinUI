// Harness.WinUI — Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Harness.Browser;

/// <summary>
/// A browser the agent (or a replay) drives: Playwright's Chromium, using the Edge or Chrome that is already on the PC so nothing is downloaded
/// (HARNESS_BROWSER_CHANNEL changes it; an empty value uses Playwright's own Chromium, which must have been installed). When hosts are limited, a page
/// load that would leave them is stopped and remembered in <see cref="BlockedUrl"/>.
/// </summary>
internal sealed class BrowserSession : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public IPage Page { get; private set; } = null!;

    /// <summary>The first address a page load was stopped for because it is not in the list of allowed hosts; null while nothing was stopped.</summary>
    public string? BlockedUrl { get; private set; }

    public static string Channel => Environment.GetEnvironmentVariable("HARNESS_BROWSER_CHANNEL") is { } c ? c.Trim() : "msedge";

    public static async Task<BrowserSession> StartAsync(BrowserOptions options, bool headless)
    {
        var session = new BrowserSession();
        try
        {
            session._playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            var launch = new BrowserTypeLaunchOptions { Headless = headless };
            if (Channel.Length > 0)
                launch.Channel = Channel;
            session._browser = await session._playwright.Chromium.LaunchAsync(launch).ConfigureAwait(false);
            var context = await session._browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 }, Locale = "zh-TW" }).ConfigureAwait(false);
            session.Page = await context.NewPageAsync().ConfigureAwait(false);
            session.Page.SetDefaultTimeout(8000);
            session.Page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            if (options.HasLimit)
            {
                await context.RouteAsync("**/*", async route =>
                {
                    if (route.Request.IsNavigationRequest && !options.Allows(route.Request.Url))
                    {
                        session.BlockedUrl ??= route.Request.Url;
                        await route.AbortAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await route.ContinueAsync().ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser is not null)
                await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (Failures.Is(failure))
        {
            // already closed by the person
        }
        _playwright?.Dispose();
    }
}

/// <summary>What counts as a failure of the browser or the page: Playwright reports a timeout (an element that never appears) as a TimeoutException, not a PlaywrightException.</summary>
internal static class Failures
{
    public static bool Is(Exception ex) => ex is PlaywrightException or TimeoutException;
}

/// <summary>The checks the model can ask for. The same code runs while exploring (to prove a check before it is recorded) and in every replay.</summary>
internal static class Asserts
{
    /// <summary>Runs the check; false with what was seen when it does not hold. An element check needs <paramref name="locator"/>.</summary>
    public static async Task<(bool Ok, string Actual)> VerifyAsync(IPage page, AssertSpec spec, ILocator? locator, int timeoutMs = 3000)
    {
        try
        {
            switch (spec.Type)
            {
                case "text_visible":
                    await Assertions.Expect(page.Locator("body")).ToContainTextAsync(spec.Text, new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                case "url_contains":
                    await Assertions.Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(spec.Text)), new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                case "title_contains":
                    await Assertions.Expect(page).ToHaveTitleAsync(new Regex(Regex.Escape(spec.Text)), new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                case "element_visible":
                    await Assertions.Expect(Need(locator, spec)).ToBeVisibleAsync(new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                case "element_text":
                    await Assertions.Expect(Need(locator, spec)).ToContainTextAsync(spec.Text, new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                case "element_value":
                    await Assertions.Expect(Need(locator, spec)).ToHaveValueAsync(spec.Text, new() { Timeout = timeoutMs }).ConfigureAwait(false);
                    break;
                default:
                    return (false, $"unknown assert type \"{spec.Type}\"; use one of: {string.Join(", ", AssertSpec.Types)}");
            }
            return (true, "");
        }
        catch (Exception ex) when (Failures.Is(ex))
        {
            return (false, string.Join(' ', ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4)));
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

        static ILocator Need(ILocator? locator, AssertSpec spec) => locator ?? throw new InvalidOperationException($"{spec.Type} needs an element id");
    }
}
