// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Observability;
using Microsoft.Extensions.AI;

namespace Harness.Core.MiniApps;

/// <summary>Error codes a Mini App's page gets back from the bridge.</summary>
public static class MiniAppErrors
{
    public const string PermissionNotRequested = "PERMISSION_NOT_REQUESTED";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string NoModel = "NO_MODEL";
    public const string Busy = "BUSY";
    public const string Failed = "FAILED";
}

/// <summary>
/// What a Mini App's page may ask of Harness, one request at a time: <c>{ id, method, params }</c> in, and back
/// <c>{ id, result }</c> or <c>{ id, error: { code, message } }</c> (a streamed reply first sends <c>{ id, delta }</c>).
/// Everything the page sends is checked here: the page is not trusted with a key, a path or a command.
/// </summary>
public sealed class MiniAppBridge(
    string appId,
    MiniAppStore store,
    Func<(IChatClient? Client, string? Model)> model,
    Action<string?, long, long> recordUsage,
    Action<string> openLink,
    MiniAppAudit? audit = null) : IDisposable
{
    public const int MaxMessages = 40;
    public const int MaxChars = 60_000;
    public const int MaxSystemChars = 8_000;
    public const int DefaultOutputTokens = 1_000;
    public const int MaxOutputTokens = 4_000;

    private readonly SemaphoreSlim _llm = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Answers one request from the page. <paramref name="post"/> sends a JSON message back to it.</summary>
    public async Task HandleAsync(string requestJson, Func<string, Task> post)
    {
        JsonNode? id = null;
        string method = string.Empty;
        try
        {
            var request = JsonNode.Parse(requestJson) as JsonObject ?? throw new JsonException();
            id = request["id"]?.DeepClone();
            method = request["method"]?.GetValue<string>() ?? string.Empty;
            if (id is null)
                return; // nothing to answer to
            var parameters = request["params"] as JsonObject ?? [];

            // The app and its version are read now, not when the window opened: an update or a revoked permission counts at once.
            if (store.Get(appId) is not { } app)
            {
                await Error(post, id, method, MiniAppErrors.Failed, "The app is no longer installed.");
                return;
            }

            switch (method)
            {
                case "llm.chat":
                    await ChatAsync(app, id, parameters, post);
                    break;
                case "open.link":
                    OpenLink(app, id, parameters, post);
                    break;
                default:
                    await Error(post, id, method, MiniAppErrors.MethodNotFound, $"Unknown method '{method}'.");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            if (id is not null)
                await Error(post, id, method, MiniAppErrors.InvalidParams, "The request is not valid.");
        }
    }

    private async Task ChatAsync(MiniAppInfo app, JsonNode id, JsonObject parameters, Func<string, Task> post)
    {
        if (Check(app, "llm") is { } denied)
        {
            await Error(post, id, "llm.chat", denied.Code, denied.Message);
            return;
        }

        List<ChatMessage> messages = [];
        var chars = 0;
        if (parameters["system"] is JsonValue systemValue && systemValue.TryGetValue<string>(out var system) && system.Length > 0)
        {
            if (system.Length > MaxSystemChars)
            {
                await Error(post, id, "llm.chat", MiniAppErrors.InvalidParams, "The system text is too long.");
                return;
            }

            messages.Add(new ChatMessage(ChatRole.System, system));
            chars += system.Length;
        }

        if (parameters["messages"] is not JsonArray array || array.Count is 0 or > MaxMessages)
        {
            await Error(post, id, "llm.chat", MiniAppErrors.InvalidParams, $"messages must have 1 to {MaxMessages} entries.");
            return;
        }

        foreach (var item in array)
        {
            var role = item?["role"]?.GetValue<string>();
            var content = item?["content"]?.GetValue<string>();
            if (role is not ("user" or "assistant") || content is null)
            {
                await Error(post, id, "llm.chat", MiniAppErrors.InvalidParams, "Each message needs a role (user or assistant) and text content.");
                return;
            }

            chars += content.Length;
            messages.Add(new ChatMessage(role == "user" ? ChatRole.User : ChatRole.Assistant, content));
        }

        if (chars > MaxChars)
        {
            await Error(post, id, "llm.chat", MiniAppErrors.InvalidParams, "The messages are too long.");
            return;
        }

        var maxTokens = DefaultOutputTokens;
        if (parameters["maxTokens"] is JsonValue tokens && tokens.TryGetValue<int>(out var asked))
            maxTokens = Math.Clamp(asked, 1, MaxOutputTokens);
        var stream = parameters["stream"] is JsonValue flag && flag.TryGetValue<bool>(out var s) && s;

        var (client, modelName) = model();
        if (client is null)
        {
            await Error(post, id, "llm.chat", MiniAppErrors.NoModel, "No model is set up in Harness.WinUI.");
            return;
        }

        // One call at a time per app: a page in a loop can't pile up requests (and cost).
        if (!await _llm.WaitAsync(0))
        {
            await Error(post, id, "llm.chat", MiniAppErrors.Busy, "A model call is already running.");
            return;
        }

        var summary = $"messages={messages.Count} chars={chars} stream={stream}";

        // Each call is a turn of its own in the observability records (named after the app), with its model call under it.
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var turn = Telemetry.StartTurn($"[{app.Name}] {(lastUser.Length > 300 ? lastUser[..300] : lastUser)}");
        if (turn is not null)
            turn.DisplayName = "app:" + app.Name; // the name is kept even when message content is not, for the filter
        var reply = string.Empty;
        var stopped = false;
        string? failure = null;
        try
        {
            var options = new ChatOptions { MaxOutputTokens = maxTokens };
            string text;
            UsageDetails? usage = null;
            if (stream)
            {
                var all = new System.Text.StringBuilder();
                string? streamError = null;
                ChatFinishReason? finish = null;
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, _closing.Token))
                {
                    if (update.Text is { Length: > 0 } piece)
                    {
                        all.Append(piece);
                        await post(new JsonObject { ["id"] = id.DeepClone(), ["delta"] = piece }.ToJsonString());
                    }

                    if (update.FinishReason is { } reason)
                        finish = reason;
                    foreach (var content in update.Contents)
                    {
                        if (content is UsageContent u)
                            usage = u.Details;
                        else if (content is ErrorContent e)
                            streamError = e.Message;
                    }
                }

                text = all.ToString();
                if (text.Length == 0)
                    throw new InvalidOperationException(streamError ?? $"The model answered nothing (finish reason: {finish?.Value ?? "none"}).");
            }
            else
            {
                var response = await client.GetResponseAsync(messages, options, _closing.Token);
                text = response.Text;
                usage = response.Usage;
                if (text.Length == 0)
                    throw new InvalidOperationException($"The model answered nothing (finish reason: {response.FinishReason?.Value ?? "none"}).");
            }

            reply = text;
            var input = usage?.InputTokenCount ?? 0;
            var output = usage?.OutputTokenCount ?? 0;
            recordUsage(modelName, input, output);
            audit?.Write(app, "llm.chat", $"{summary} in={input} out={output}", ok: true);
            await post(new JsonObject
            {
                ["id"] = id.DeepClone(),
                ["result"] = new JsonObject { ["text"] = text, ["inputTokens"] = input, ["outputTokens"] = output },
            }.ToJsonString());
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            audit?.Write(app, "llm.chat", summary, ok: false, "cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex.Message;
            audit?.Write(app, "llm.chat", summary, ok: false, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
            await post(Reply(id, error: (MiniAppErrors.Failed, "The model call failed: " + ex.Message)));
        }
        finally
        {
            Telemetry.EndTurn(turn, "miniapp:" + app.Id, reply, stopped, failure);
            _llm.Release();
        }
    }

    private void OpenLink(MiniAppInfo app, JsonNode id, JsonObject parameters, Func<string, Task> post)
    {
        var url = parameters["url"]?.GetValue<string>();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _ = Error(post, id, "open.link", MiniAppErrors.InvalidParams, "Only http and https links can be opened.");
            return;
        }

        openLink(uri.AbsoluteUri);
        audit?.Write(app, "open.link", uri.Host, ok: true);
        _ = post(Reply(id, result: new JsonObject { ["opened"] = true }));
    }

    /// <summary>Null when the app may use this permission now.</summary>
    private static (string Code, string Message)? Check(MiniAppInfo app, string permission)
    {
        if (!app.Permissions.Contains(permission))
            return (MiniAppErrors.PermissionNotRequested, $"The app did not ask for '{permission}' in its manifest.");
        if (!app.IsApproved)
            return (MiniAppErrors.PermissionDenied, $"'{permission}' has not been allowed for this version of the app.");
        return null;
    }

    private async Task Error(Func<string, Task> post, JsonNode id, string method, string code, string message)
    {
        if (store.Get(appId) is { } app)
            audit?.Write(app, method, string.Empty, ok: false, code);
        await post(Reply(id, error: (code, message)));
    }

    private static string Reply(JsonNode id, JsonNode? result = null, (string Code, string Message)? error = null)
    {
        var reply = new JsonObject { ["id"] = id.DeepClone() };
        if (error is { } e)
            reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else
            reply["result"] = result;
        return reply.ToJsonString();
    }

    /// <summary>The window is closing: a model call still running is stopped.</summary>
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        _llm.Dispose();
    }
}
