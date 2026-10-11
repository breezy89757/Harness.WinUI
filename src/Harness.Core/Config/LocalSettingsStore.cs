// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Core.Config;

/// <summary>One saved connection: where the model is, which model, and the key for it.</summary>
/// <param name="Name">Optional label shown in the connection list; blank means "model · host".</param>
/// <param name="ApiKey">The decrypted key; empty for a connection still being filled in.</param>
public sealed record ProviderProfile(string Name, string Endpoint, string Model, string ApiKey, ProviderApi Api, string? ImageModel);

/// <summary>
/// Reads/writes the user-editable provider settings (endpoint, model, API key) entered through
/// the app's Settings UI. Stored in <c>settings.json</c> in the data folder (<see cref="AppPaths"/>). The endpoint and
/// model are plain text (not secrets); the API key is encrypted with DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>) before it touches
/// disk, so the file only ever contains a blob that's meaningless outside this Windows user
/// account — never a plaintext key.
///
/// Several connections can be saved (e.g. a cloud service and a local Ollama); one is active. The top-level
/// fields of the file always mirror the active connection, exactly as the single-connection format had them, so an
/// older version of the app still reads the active one and a file from before this change loads as one connection.
///
/// This is a separate, higher-priority source of provider config from <see cref="ProviderOptions"/>
/// (which is bound from appsettings.json + an env-var-named key, and stays useful for local
/// development / CI without going through the UI). <see cref="ProviderResolver"/> combines both.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LocalSettingsStore
{
    // Keeps the app's original name on purpose: changing the entropy would make saved keys unreadable.
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("Yoke.ProviderApiKey.v1");

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string SettingsPath => AppPaths.Combine("settings.json");

    /// <summary>Loads and decrypts the active saved provider, or null if nothing usable is saved.</summary>
    public static ResolvedProvider? Load()
    {
        var (profiles, active) = LoadProfiles();
        if (profiles.Count == 0)
            return null;

        var profile = profiles[active];
        return new ResolvedProvider(
            profile.Endpoint, profile.Model, profile.ApiKey, profile.Api, ProviderSource.LocalEncryptedSettings, profile.ImageModel);
    }

    /// <summary>All usable saved connections and the index of the active one; empty if nothing usable is saved.</summary>
    public static (IReadOnlyList<ProviderProfile> Profiles, int Active) LoadProfiles()
    {
        if (!File.Exists(SettingsPath))
            return ([], 0);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredProviderSettings>(File.ReadAllText(SettingsPath));
            if (stored is null)
                return ([], 0);

            // A file from before connections were a list is one connection.
            var entries = stored.Profiles is { Count: > 0 } ? stored.Profiles : [stored];
            var profiles = new List<ProviderProfile>();
            var active = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                // One that can't be read (a key from another user profile/machine, a half-written entry) is left
                // out rather than failing the others; the Settings UI lets the user re-enter it.
                if (ToProfile(entries[i]) is not { } profile)
                    continue;
                if (i == stored.ActiveProfile)
                    active = profiles.Count;
                profiles.Add(profile);
            }
            return (profiles, active);
        }
        catch
        {
            // Corrupted file etc. — treat as "not configured" rather than crashing the app.
            return ([], 0);
        }
    }

    private static ProviderProfile? ToProfile(StoredProviderSettings stored)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stored.ProtectedApiKeyBase64))
                return null;

            var cipher = Convert.FromBase64String(stored.ProtectedApiKeyBase64);
            var apiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.CurrentUser));

            if (string.IsNullOrWhiteSpace(stored.Endpoint) || string.IsNullOrWhiteSpace(stored.Model) || string.IsNullOrWhiteSpace(apiKey))
                return null;

            return new ProviderProfile(
                stored.Name?.Trim() ?? string.Empty, stored.Endpoint, stored.Model, apiKey, ProviderApiExtensions.Parse(stored.Api),
                string.IsNullOrWhiteSpace(stored.ImageModel) ? null : stored.ImageModel);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves every connection and which one is active (an index into <paramref name="profiles"/>).</summary>
    public static void Save(IReadOnlyList<ProviderProfile> profiles, int active)
    {
        ArgumentOutOfRangeException.ThrowIfZero(profiles.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(active);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(active, profiles.Count);

        var entries = profiles.Select(ToStored).ToList();
        var stored = entries[active] with { Profiles = entries, ActiveProfile = active };

        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(stored, s_json));
    }

    private static StoredProviderSettings ToStored(ProviderProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ApiKey);

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(profile.ApiKey), s_entropy, DataProtectionScope.CurrentUser);
        return new StoredProviderSettings
        {
            Name = string.IsNullOrWhiteSpace(profile.Name) ? null : profile.Name.Trim(),
            Endpoint = profile.Endpoint.Trim(),
            Model = profile.Model.Trim(),
            Api = profile.Api.ToString(),
            ImageModel = string.IsNullOrWhiteSpace(profile.ImageModel) ? null : profile.ImageModel.Trim(),
            ProtectedApiKeyBase64 = Convert.ToBase64String(cipher),
        };
    }

    private sealed record StoredProviderSettings
    {
        public string? Name { get; init; }
        public string Endpoint { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string Api { get; init; } = nameof(ProviderApi.Auto);
        public string? ImageModel { get; init; }

        /// <summary>Base64 of the DPAPI-protected API key bytes. Never a plaintext key.</summary>
        public string ProtectedApiKeyBase64 { get; init; } = string.Empty;

        /// <summary>Every saved connection (the top-level fields above repeat the active one); only set on the top level.</summary>
        public List<StoredProviderSettings>? Profiles { get; init; }

        public int ActiveProfile { get; init; }
    }
}
