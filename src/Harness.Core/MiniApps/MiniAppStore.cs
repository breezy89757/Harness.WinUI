// Harness.WinUI — Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Config;

namespace Harness.Core.MiniApps;

/// <summary>An installed Mini App: the version it is on and what that version asked for.</summary>
/// <param name="Id">A GUID chosen at install (never the artifact's id, which the model picks and may repeat).</param>
/// <param name="VersionHash">A hash of everything the version holds; any change makes a new version.</param>
/// <param name="SourceArtifactId">The artifact it was installed from, to offer updates when that artifact changes.</param>
/// <param name="ApprovedHash">The version whose requested permissions the person allowed; a new version has to be allowed again.</param>
public sealed record MiniAppInfo(
    string Id, string Name, string VersionHash, IReadOnlyList<string> Permissions, IReadOnlyList<string> AllowedHosts,
    string? SourceArtifactId, DateTimeOffset InstalledAt, string? ApprovedHash = null)
{
    /// <summary>The page's host: each app has an origin of its own, so storage and cookies never meet.</summary>
    public const string HostSuffix = "harness.miniapps";

    /// <summary>The person allowed what this version asks for (always true for a version that asks for nothing).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsApproved => Permissions.Count == 0 || ApprovedHash == VersionHash;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Host =>$"{Id}.{HostSuffix}";
}

/// <summary>
/// The installed Mini Apps, kept apart from the artifacts folder (<c>apps\{id}\app.json</c> and one folder per version:
/// <c>apps\{id}\versions\{hash}\index.html</c>), so what an app is stays put when a conversation's artifact is replaced.
/// A version's folder is never rewritten; an update adds a folder and moves <c>app.json</c> to it.
/// </summary>
public sealed partial class MiniAppStore(string? root = null)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    public string Root { get; } = root ?? AppPaths.Combine("apps");

    public static bool IsValidId(string? id) => id is not null && AppId().IsMatch(id);

    /// <summary>The hash of a version's content (and with it the manifest, which is part of the page).</summary>
    public static string ComputeHash(string html) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..32];

    /// <summary>Installs a page as a new app.</summary>
    public MiniAppInfo Install(string html, string? sourceArtifactId, string? fallbackName = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return Save(id, html, sourceArtifactId, fallbackName, DateTimeOffset.UtcNow);
    }

    /// <summary>A new version of an installed app; the same content is not a new version. Null when there is no such app.</summary>
    public MiniAppInfo? Update(string id, string html)
    {
        lock (_gate)
        {
            if (Get(id) is not { } current)
                return null;
            return ComputeHash(html) == current.VersionHash ? current : Save(id, html, current.SourceArtifactId, current.Name, current.InstalledAt, current.ApprovedHash);
        }
    }

    public MiniAppInfo? Get(string id)
    {
        if (!IsValidId(id))
            return null;
        try
        {
            var file = Path.Combine(Root, id, "app.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<MiniAppInfo>(File.ReadAllText(file), s_json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The installed apps, by name.</summary>
    public IReadOnlyList<MiniAppInfo> List()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.EnumerateDirectories(Root)
            .Select(d => Get(Path.GetFileName(d)))
            .OfType<MiniAppInfo>()
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The page of the app's current version, or null.</summary>
    public string? ReadIndex(string id) => ReadVersion(id, null);

    /// <summary>The page of one version (the current one when <paramref name="versionHash"/> is null).</summary>
    public string? ReadVersion(string id, string? versionHash)
    {
        if (Get(id) is not { } app)
            return null;
        var hash = versionHash ?? app.VersionHash;
        if (!HashName().IsMatch(hash))
            return null;
        var file = Path.Combine(Root, id, "versions", hash, "index.html");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The folder of the app's own browser profile (its storage), kept between runs and removed with the app.</summary>
    public string ProfileFolder(string id) => Path.Combine(Root, id, "profile");

    /// <summary>The person allowed what the app's current version asks for.</summary>
    public MiniAppInfo? Approve(string id)
    {
        lock (_gate)
        {
            if (Get(id) is not { } app)
                return null;
            var approved = app with { ApprovedHash = app.VersionHash };
            var target = Path.Combine(Root, id, "app.json");
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(approved, s_json));
            File.Move(temp, target, overwrite: true);
            return approved;
        }
    }

    /// <summary>Like <see cref="Remove"/>, trying again for a moment: the app's browser may still hold its profile as its window closes.</summary>
    public async Task<bool> RemoveAsync(string id)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (Remove(id) || !Directory.Exists(Path.Combine(Root, id)))
                return true;
            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>Removes the app with all its versions and its own data.</summary>
    public bool Remove(string id)
    {
        if (!IsValidId(id))
            return false;
        lock (_gate)
        {
            var folder = Path.Combine(Root, id);
            if (!Directory.Exists(folder))
                return false;
            try
            {
                Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private MiniAppInfo Save(string id, string html, string? sourceArtifactId, string? fallbackName, DateTimeOffset installedAt, string? approvedHash = null)
    {
        var manifest = MiniAppManifest.Parse(html, fallbackName);
        var info = new MiniAppInfo(id, manifest.Name, ComputeHash(html), manifest.Permissions, manifest.AllowedHosts, sourceArtifactId, installedAt, approvedHash);

        lock (_gate)
        {
            var versionFolder = Path.Combine(Root, id, "versions", info.VersionHash);
            Directory.CreateDirectory(versionFolder);
            var page = Path.Combine(versionFolder, "index.html");
            if (!File.Exists(page))
                File.WriteAllText(page, html);

            // app.json last, through a temp file: an app is never listed before its page is on disk.
            var target = Path.Combine(Root, id, "app.json");
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, s_json));
            File.Move(temp, target, overwrite: true);
        }

        return info;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex AppId();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HashName();
}
