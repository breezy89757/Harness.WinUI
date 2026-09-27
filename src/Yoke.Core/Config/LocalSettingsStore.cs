// Yoke — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yoke.Core.Config;

/// <summary>
/// Reads/writes the user-editable provider settings (endpoint, model, API key) entered through
/// the app's Settings UI. Stored in <c>settings.json</c> in the data folder (<see cref="AppPaths"/>). The endpoint and
/// model are plain text (not secrets); the API key is encrypted with DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>) before it touches
/// disk, so the file only ever contains a blob that's meaningless outside this Windows user
/// account — never a plaintext key.
///
/// This is a separate, higher-priority source of provider config from <see cref="ProviderOptions"/>
/// (which is bound from appsettings.json + an env-var-named key, and stays useful for local
/// development / CI without going through the UI). <see cref="ProviderResolver"/> combines both.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LocalSettingsStore
{
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("Yoke.ProviderApiKey.v1");

    private static string SettingsPath => AppPaths.Combine("settings.json");

    /// <summary>Loads and decrypts the saved provider, or null if nothing usable is saved.</summary>
    public static ResolvedProvider? Load()
    {
        if (!File.Exists(SettingsPath))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredProviderSettings>(File.ReadAllText(SettingsPath));
            if (stored is null || string.IsNullOrWhiteSpace(stored.ProtectedApiKeyBase64))
                return null;

            var cipher = Convert.FromBase64String(stored.ProtectedApiKeyBase64);
            var apiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.CurrentUser));

            if (string.IsNullOrWhiteSpace(stored.Endpoint) || string.IsNullOrWhiteSpace(stored.Model) || string.IsNullOrWhiteSpace(apiKey))
                return null;

            return new ResolvedProvider(
                stored.Endpoint, stored.Model, apiKey, ProviderApiExtensions.Parse(stored.Api), ProviderSource.LocalEncryptedSettings,
                string.IsNullOrWhiteSpace(stored.ImageModel) ? null : stored.ImageModel);
        }
        catch
        {
            // Corrupted file, DPAPI blob from a different user profile/machine, etc. — treat as
            // "not configured" rather than crashing the app; the Setup UI lets the user re-enter.
            return null;
        }
    }

    public static void Save(string endpoint, string model, string apiKey, ProviderApi api, string? imageModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var plainBytes = Encoding.UTF8.GetBytes(apiKey);
        var cipher = ProtectedData.Protect(plainBytes, s_entropy, DataProtectionScope.CurrentUser);

        var stored = new StoredProviderSettings
        {
            Endpoint = endpoint.Trim(),
            Model = model.Trim(),
            Api = api.ToString(),
            ImageModel = string.IsNullOrWhiteSpace(imageModel) ? null : imageModel.Trim(),
            ProtectedApiKeyBase64 = Convert.ToBase64String(cipher),
        };

        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record StoredProviderSettings
    {
        public string Endpoint { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string Api { get; init; } = nameof(ProviderApi.Auto);
        public string? ImageModel { get; init; }

        /// <summary>Base64 of the DPAPI-protected API key bytes. Never a plaintext key.</summary>
        public string ProtectedApiKeyBase64 { get; init; } = string.Empty;
    }
}
