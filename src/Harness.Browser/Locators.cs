// Harness.WinUI — Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Harness.Browser;

/// <summary>
/// Turns an element number into a locator that finds that one element again, in the order of preference: role + name, label, placeholder,
/// test id, text, and only then CSS or XPath. The model never writes a selector. The same locator is used to act now and in every replay.
/// </summary>
internal static partial class Locators
{
    private static readonly HashSet<string> s_knownRoles = new(StringComparer.Ordinal)
    {
        "button", "link", "textbox", "checkbox", "radio", "combobox", "listbox", "searchbox", "spinbutton", "slider", "tab", "menuitem", "switch", "option",
    };

    /// <summary>The ways to find this element, most stable first (not yet checked against the page: see <see cref="ResolveAsync"/>).</summary>
    public static IReadOnlyList<LocatorInfo> Candidates(PageElement e)
    {
        var found = new List<LocatorInfo>();
        if (s_knownRoles.Contains(e.Role))
        {
            foreach (var name in e.Names)
                found.Add(new LocatorInfo("role", e.Role, name, null, "role + accessible name"));
        }
        if (e.Label.Length > 0)
            found.Add(new LocatorInfo("label", null, e.Label, null, "label text"));
        if (e.Placeholder.Length > 0)
            found.Add(new LocatorInfo("placeholder", null, e.Placeholder, null, "placeholder"));
        if (e.TestId.Length > 0)
            found.Add(new LocatorInfo("testid", null, e.TestId, null, "test id"));
        if (e.Text.Length > 0 && e.Tag is not ("input" or "textarea" or "select"))
            found.Add(new LocatorInfo("text", null, e.Text, null, "visible text"));
        if (e.Id.Length > 0 && IdWord().IsMatch(e.Id))
            found.Add(new LocatorInfo("css", null, "#" + e.Id, null, "element id"));
        return found;
    }

    [GeneratedRegex(@"^[A-Za-z][\w-]*$")]
    private static partial Regex IdWord();

    public static ILocator ToLocator(IPage page, LocatorInfo info)
    {
        ILocator locator = info.Strategy switch
        {
            "role" => page.GetByRole(Enum.TryParse<AriaRole>(info.Role, true, out var role) ? role : throw new InvalidOperationException($"unknown role {info.Role}"), new() { Name = info.Value, Exact = true }),
            "label" => page.GetByLabel(info.Value, new() { Exact = true }),
            "placeholder" => page.GetByPlaceholder(info.Value, new() { Exact = true }),
            "testid" => page.GetByTestId(info.Value),
            "text" => page.GetByText(info.Value, new() { Exact = true }),
            "xpath" => page.Locator("xpath=" + info.Value),
            _ => page.Locator(info.Value),
        };
        return info.Nth is { } nth ? locator.Nth(nth) : locator;
    }

    private const string CssPathJs = """
        (e) => {
          const parts = [];
          for (; e && e.nodeType === 1 && e.tagName !== 'HTML'; e = e.parentElement) {
            const same = [...e.parentElement.children].filter(c => c.tagName === e.tagName);
            parts.unshift(e.tagName.toLowerCase() + (same.length > 1 ? `:nth-of-type(${same.indexOf(e) + 1})` : ''));
          }
          return 'html > ' + parts.join(' > ');
        }
        """;

    /// <summary>The first candidate that finds exactly this one element; else the first that finds it among several (with its position); else a CSS path.</summary>
    public static async Task<(LocatorInfo Info, ILocator Locator)> ResolveAsync(IPage page, PageElement element)
    {
        var refText = element.Ref.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LocatorInfo? among = null;
        foreach (var candidate in Candidates(element))
        {
            var locator = ToLocator(page, candidate);
            int count;
            try
            {
                count = await locator.CountAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (Failures.Is(failure))
            {
                continue;
            }
            try
            {
                if (count == 1 && await locator.GetAttributeAsync("data-hqa-ref", new() { Timeout = 1500 }).ConfigureAwait(false) == refText)
                    return (candidate with { Reason = candidate.Reason + " is unique on the page" }, locator);
                if (count is > 1 and <= 12 && among is null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (await locator.Nth(i).GetAttributeAsync("data-hqa-ref", new() { Timeout = 1500 }).ConfigureAwait(false) == refText)
                        {
                            among = candidate with { Nth = i, Reason = $"{candidate.Reason} matches {count} elements; this is number {i + 1}" };
                            break;
                        }
                    }
                }
            }
            catch (Exception failure) when (Failures.Is(failure))
            {
                // an element that disappears while it is being looked at: try the next way of finding it
            }
        }
        if (among is not null)
            return (among, ToLocator(page, among));

        var path = await page.Locator($"[data-hqa-ref='{refText}']").EvaluateAsync<string>(CssPathJs).ConfigureAwait(false);
        var css = new LocatorInfo("css", null, path, null, "no role, label, placeholder, test id or text found it alone: CSS path as the last resort");
        return (css, ToLocator(page, css));
    }
}
