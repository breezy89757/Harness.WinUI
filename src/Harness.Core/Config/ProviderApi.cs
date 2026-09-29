// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Config;

/// <summary>Which OpenAI wire API to call.</summary>
public enum ProviderApi
{
    /// <summary>Responses for Azure OpenAI / api.openai.com, Chat Completions for anything else (e.g. LiteLLM).</summary>
    Auto,

    /// <summary><c>/v1/responses</c> — needed for reasoning models to use tools (e.g. gpt-6-luna rejects
    /// tools + reasoning on Chat Completions), and streams reasoning summaries.</summary>
    Responses,

    /// <summary><c>/v1/chat/completions</c> — the most widely supported, e.g. by LiteLLM gateways.</summary>
    ChatCompletions,
}

public static class ProviderApiExtensions
{
    public static ProviderApi Parse(string? value) => value?.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant() switch
    {
        "responses" => ProviderApi.Responses,
        "chatcompletions" or "chat" => ProviderApi.ChatCompletions,
        _ => ProviderApi.Auto,
    };

    /// <summary>Resolves <see cref="ProviderApi.Auto"/> from the endpoint host.</summary>
    public static ProviderApi Resolve(this ProviderApi api, Uri endpoint)
    {
        if (api != ProviderApi.Auto)
            return api;

        var host = endpoint.Host;
        var isOpenAiService =
            host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);

        return isOpenAiService ? ProviderApi.Responses : ProviderApi.ChatCompletions;
    }
}
