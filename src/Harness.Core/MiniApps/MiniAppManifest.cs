// Harness.WinUI — Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Tools;

namespace Harness.Core.MiniApps;

/// <summary>How much a permission lets a Mini App reach (see the grants).</summary>
public enum MiniAppTier
{
    /// <summary>Asked once, when the app is installed.</summary>
    Basic,

    /// <summary>Asked the first time the app uses it.</summary>
    System,

    /// <summary>Off unless turned on in Settings and for the app; every call is approved.</summary>
    Full,
}

public static class MiniAppPermissions
{
    private static readonly Dictionary<string, MiniAppTier> s_tiers = new(StringComparer.Ordinal)
    {
        ["llm"] = MiniAppTier.Basic,
        ["storage"] = MiniAppTier.Basic,
        ["mcp"] = MiniAppTier.Basic,
        ["notify"] = MiniAppTier.System,
        ["hotkey"] = MiniAppTier.System,
        ["alwaysOnTop"] = MiniAppTier.System,
        ["tray"] = MiniAppTier.System,
        ["fs"] = MiniAppTier.Full,
        ["proc"] = MiniAppTier.Full,
    };

    public static bool IsKnown(string permission) => s_tiers.ContainsKey(permission);

    public static MiniAppTier? TierOf(string permission) => s_tiers.TryGetValue(permission, out var tier) ? tier : null;
}

/// <summary>
/// What a page says about itself in <c>&lt;script type="application/json" id="harness-app"&gt;</c>: a name, the permissions it
/// would like and the hosts it may talk to. These are requests: whether they are granted is the person's decision.
/// A page without the block gets its <c>&lt;title&gt;</c> as the name and no permissions beyond the plain page.
/// </summary>
public sealed partial record MiniAppManifest(string Name, IReadOnlyList<string> Permissions, IReadOnlyList<string> AllowedHosts)
{
    public const int MaxNameLength = 60;

    /// <summary>Permissions in the manifest that this version does not know (kept so the person can be told, never granted).</summary>
    public IReadOnlyList<string> UnknownPermissions { get; init; } = [];

    /// <summary>The Content-Security-Policy the app's page is served with: its own files, and only the declared hosts besides.</summary>
    public string Csp() => McpApps.BuildCsp(AllowedHosts, AllowedHosts, [], []);

    public static MiniAppManifest Parse(string html, string? fallbackName = null)
    {
        string? name = null;
        List<string> permissions = [], unknown = [], hosts = [];

        var json = ManifestJson(html);
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        name = n.GetString();
                    foreach (var value in Strings(root, "permissions"))
                    {
                        if (!MiniAppPermissions.IsKnown(value))
                            unknown.Add(value);
                        else if (!permissions.Contains(value))
                            permissions.Add(value);
                    }

                    // Only plain origins: anything else could carry extra directives into the policy.
                    hosts = Strings(root, "allowedHosts").Select(h => h.Trim()).Where(h => McpApps.IsCspSource(h)).Distinct().ToList();
                }
            }
            catch (JsonException)
            {
                // A broken block is the same as none.
            }
        }

        name = Clean(name) ?? Clean(Title(html)) ?? Clean(fallbackName) ?? "App";
        return new MiniAppManifest(name, permissions, hosts) { UnknownPermissions = unknown };
    }

    private static IEnumerable<string> Strings(JsonElement root, string property) =>
        root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    private static string? Title(string html)
    {
        var match = TitleTag().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxNameLength)
            cleaned = cleaned[..MaxNameLength];
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"<script\b([^>]*)>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"(?<![\w-])id\s*=\s*[""']harness-app[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestId();

    // The first script that is the manifest: id="harness-app" and a JSON type.
    private static string? ManifestJson(string html) =>
        ScriptTag().Matches(html)
            .FirstOrDefault(m => ManifestId().IsMatch(m.Groups[1].Value) && m.Groups[1].Value.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            ?.Groups[2].Value;

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();
}
