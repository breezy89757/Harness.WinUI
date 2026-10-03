// Harness.WinUI — Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using Harness.Core.Config;
using Harness.Core.Observability;

namespace Harness.Core.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> against an OpenAI wire-protocol-compatible endpoint
/// (Azure OpenAI's <c>/openai/v1</c> surface, api.openai.com, or a LiteLLM gateway), using either
/// the Responses or the Chat Completions API — see <see cref="ProviderApi"/>.
/// </summary>
public static class ChatClientFactory
{
    /// <param name="instrument">False for calls that shouldn't appear in the observability records (e.g. analysing them).</param>
    public static IChatClient Create(ResolvedProvider provider, bool instrument = true) =>
        Create(provider.Endpoint, provider.Model, provider.ApiKey, provider.Api, instrument);

    /// <summary>Reasoning summaries only exist on the Responses API; don't send the option elsewhere (e.g. LiteLLM).</summary>
    public static bool SupportsReasoningSummaries(ResolvedProvider provider) =>
        Uri.TryCreate(provider.Endpoint.Trim(), UriKind.Absolute, out var endpoint) &&
        provider.Api.Resolve(endpoint) == ProviderApi.Responses;

    /// <summary>Builds a client from an already-resolved endpoint/model/API key.</summary>
    public static IChatClient Create(string endpoint, string model, string apiKey, ProviderApi api = ProviderApi.Auto, bool instrument = true)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderConfigurationException("Endpoint, Model, and API key must all be provided.");

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var endpointUri))
            throw new ProviderConfigurationException($"Endpoint '{endpoint}' is not a valid absolute URI.");

        var credential = new ApiKeyCredential(apiKey);
        // Calls go through Telemetry's HttpClient so that, when recording is on, the raw exchange is kept too.
        var transport = instrument ? new HttpClientPipelineTransport(Telemetry.HttpClient) : null;
        var client = api.Resolve(endpointUri) switch
        {
            // The Responses client is still flagged experimental in OpenAI 2.x (OPENAI001), but it's
            // the only way reasoning models accept tools — see ProviderApi.Responses.
#pragma warning disable OPENAI001
            ProviderApi.Responses => new ResponsesClient(credential, new ResponsesClientOptions { Endpoint = endpointUri, Transport = transport })
                .AsIChatClient(model),
#pragma warning restore OPENAI001
            _ => new ChatClient(model, credential, new OpenAIClientOptions { Endpoint = endpointUri, Transport = transport })
                .AsIChatClient(),
        };
        return instrument ? Telemetry.Instrument(client) : client;
    }

    /// <summary>Dev/CI path: reads the API key from <see cref="ProviderOptions.ApiKeyEnvVar"/> and delegates.</summary>
    public static IChatClient Create(ProviderOptions config)
    {
        if (config is null || !config.HasRequiredFields)
            throw new ProviderConfigurationException(
                "Provider config is missing Endpoint, Model, or ApiKeyEnvVar.");

        var apiKey = config.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderConfigurationException(
                $"Environment variable '{config.ApiKeyEnvVar}' is not set (or empty). " +
                "Set it before starting the app — the API key is never read from config files.");

        return Create(config.Endpoint, config.Model, apiKey, ProviderApiExtensions.Parse(config.Api));
    }
}
