// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Harness.Core.Config;

namespace Harness.Browser;

/// <summary>What the person has set for the browser agent: where it may go, and whether to keep a screenshot of each step.</summary>
/// <param name="AllowedHosts">Empty means no limit. A host also covers its subdomains.</param>
/// <param name="ScreenshotFolder">Null or empty keeps no screenshots.</param>
public sealed record BrowserOptions(IReadOnlyList<string> AllowedHosts, string? ScreenshotFolder)
{
    public static BrowserOptions None { get; } = new([], null);

    public static BrowserOptions FromPreferences()
    {
        var preferences = AppPreferences.Load();
        return new BrowserOptions(ParseHosts(preferences.BrowserAllowedHosts), string.IsNullOrWhiteSpace(preferences.BrowserScreenshotFolder) ? null : preferences.BrowserScreenshotFolder.Trim());
    }

    /// <summary>"a.com, b.com" or one per line: lower case, without a scheme or path.</summary>
    public static IReadOnlyList<string> ParseHosts(string? text) =>
        [.. (text ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => Uri.TryCreate(h.Contains("://", StringComparison.Ordinal) ? h : "https://" + h, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : h.ToLowerInvariant())
            .Distinct()];

    public bool HasLimit => AllowedHosts.Count > 0;

    /// <summary>Whether the browser may go to this address: always when there is no limit; otherwise only http(s) to a listed host or one of its subdomains. about:blank is always fine.</summary>
    public bool Allows(string url)
    {
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return !HasLimit && Uri.TryCreate(url, UriKind.Absolute, out _);
        if (!HasLimit)
            return true;
        var host = uri.Host.ToLowerInvariant();
        return AllowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }
}

/// <summary>
/// Secrets for the browser agent: named, DPAPI-protected (see <see cref="SecretStore"/>, names kept under a "browser." prefix) and written
/// in a task or a recording only as <c>{{secret:NAME}}</c>. The model sees the placeholder; the program puts the value in at the moment of typing.
/// </summary>
public static partial class BrowserSecrets
{
    public const string Prefix = "browser.";

    [GeneratedRegex(@"\{\{secret:([A-Za-z0-9_.\-]+)\}\}")]
    private static partial Regex Placeholder();

    public static string Placeholder(string name) => "{{secret:" + name + "}}";

    /// <summary>The names used in the text, without repeats.</summary>
    public static IReadOnlyList<string> NamesIn(string? text) =>
        [.. Placeholder().Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>Puts the values in; <paramref name="lookup"/> gives a value or null. Throws when a name has no value.</summary>
    public static string Resolve(string text, Func<string, string?> lookup) =>
        Placeholder().Replace(text, m => lookup(m.Groups[1].Value) ?? throw new InvalidOperationException($"The secret \"{m.Groups[1].Value}\" is not stored on this PC. Add it in Settings."));

    /// <summary>Replaces every secret's value in <paramref name="text"/> by its placeholder (longest value first; values shorter than 3 characters are left alone).</summary>
    public static string Mask(string text, IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var (name, value) in secrets.OrderByDescending(s => s.Value.Length))
        {
            if (value.Length >= 3)
                text = text.Replace(value, Placeholder(name), StringComparison.Ordinal);
        }
        return text;
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Names() => [.. SecretStore.Names(Prefix).Select(n => n[Prefix.Length..])];

    [SupportedOSPlatform("windows")]
    public static string? Get(string name) => SecretStore.TryGet(Prefix + name, out var value) ? value : null;

    [SupportedOSPlatform("windows")]
    public static void Set(string name, string value) => SecretStore.Set(Prefix + name, value);

    [SupportedOSPlatform("windows")]
    public static void Remove(string name) => SecretStore.Remove(Prefix + name);

    /// <summary>Every stored secret (name → value), for masking what goes to the model.</summary>
    [SupportedOSPlatform("windows")]
    public static Dictionary<string, string> LoadAll()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names())
        {
            if (Get(name) is { Length: > 0 } value)
                all[name] = value;
        }
        return all;
    }
}
