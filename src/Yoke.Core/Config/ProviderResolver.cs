// Yoke — Licensed under the MIT License.

using System.Runtime.Versioning;

namespace Yoke.Core.Config;

/// <summary>Where a resolved provider configuration came from — surfaced so the UI can explain itself.</summary>
public enum ProviderSource
{
    /// <summary>Entered through the app's Settings UI, key encrypted at rest via DPAPI (see <see cref="LocalSettingsStore"/>).</summary>
    LocalEncryptedSettings,

    /// <summary>appsettings.json / appsettings.local.json + an env-var-named API key (see <see cref="ProviderOptions"/>) — the dev/CI path.</summary>
    ConfigFileAndEnvVar,
}

/// <param name="ImageModel">Image model/deployment on the same endpoint (e.g. gpt-image-2); null disables image generation.</param>
public sealed record ResolvedProvider(
    string Endpoint, string Model, string ApiKey, ProviderApi Api, ProviderSource Source, string? ImageModel = null);

/// <summary>
/// Combines the two places a provider config can come from, in priority order:
/// 1. <see cref="LocalSettingsStore"/> — what the user entered in the Settings UI (encrypted key).
/// 2. <see cref="ProviderOptions"/> — appsettings.json/appsettings.local.json + an env var (dev/CI path,
///    kept working so the dev console and scripted testing don't need the UI at all).
/// Returns null if neither source has a complete, usable config — the caller (app startup) should
/// then show the Settings UI instead of the chat view.
/// </summary>
public static class ProviderResolver
{
    [SupportedOSPlatform("windows")]
    public static ResolvedProvider? TryResolve(ProviderOptions? fileOptions)
    {
        if (LocalSettingsStore.Load() is { } saved)
            return saved;

        if (fileOptions is not null && fileOptions.HasRequiredFields)
        {
            var envApiKey = fileOptions.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(envApiKey))
            {
                return new ResolvedProvider(
                    fileOptions.Endpoint, fileOptions.Model, envApiKey,
                    ProviderApiExtensions.Parse(fileOptions.Api), ProviderSource.ConfigFileAndEnvVar,
                    string.IsNullOrWhiteSpace(fileOptions.ImageModel) ? null : fileOptions.ImageModel);
            }
        }

        return null;
    }
}
