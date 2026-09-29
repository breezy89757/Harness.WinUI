// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Config;

/// <summary>
/// Connection settings for an OpenAI wire-protocol-compatible chat endpoint.
/// Works for Azure OpenAI's OpenAI-compatible surface (an endpoint ending in
/// <c>/openai/v1</c>) as well as LiteLLM / any other OpenAI-compatible gateway —
/// there is intentionally no provider-specific branching here.
/// </summary>
public sealed record ProviderOptions
{
    /// <summary>Base endpoint, e.g. <c>https://your-resource.openai.azure.com/openai/v1</c> or a LiteLLM gateway URL.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Model / deployment name passed to the chat completion request.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable that holds the API key. The key itself is never
    /// stored in config so that config files can be committed to source control.
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = string.Empty;

    /// <summary>"auto" (default), "responses" or "chat-completions" — see <see cref="ProviderApi"/>.</summary>
    public string Api { get; init; } = "auto";

    /// <summary>Optional image model/deployment on the same endpoint, e.g. "gpt-image-2".</summary>
    public string? ImageModel { get; init; }

    public bool HasRequiredFields =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(ApiKeyEnvVar);

    /// <summary>Reads the API key from the environment variable named by <see cref="ApiKeyEnvVar"/>.</summary>
    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKeyEnvVar) ? null : Environment.GetEnvironmentVariable(ApiKeyEnvVar);
}
