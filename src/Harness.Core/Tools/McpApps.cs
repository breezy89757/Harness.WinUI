// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Harness.Core.Tools;

/// <summary>
/// MCP Apps (extension <c>io.modelcontextprotocol/ui</c>, spec 2026-01-26): a tool can point at a
/// <c>ui://</c> resource holding an HTML view, which the host renders next to the tool call and talks
/// to over postMessage JSON-RPC (see <see cref="McpAppSession"/>).
/// </summary>
public static partial class McpApps
{
    public const string ExtensionId = "io.modelcontextprotocol/ui";
    public const string MimeType = "text/html;profile=mcp-app";
    public const string ProtocolVersion = "2026-01-26";

    /// <summary>
    /// The settings object advertised under <see cref="ClientCapabilities.Extensions"/>. A JsonElement:
    /// the SDK's serializer can't handle a plain Dictionary there, and the element is immutable, so it's
    /// safe to share between connections.
    /// </summary>
    public static JsonElement ClientCapability { get; } = JsonSerializer.SerializeToElement(new JsonObject { ["mimeTypes"] = new JsonArray(MimeType) });

    /// <summary>
    /// The UI a tool declares: <c>_meta.ui.resourceUri</c> (or the older flat <c>_meta["ui/resourceUri"]</c>)
    /// and <c>_meta.ui.visibility</c>; null when the tool has no UI.
    /// </summary>
    public static McpAppToolUi? GetToolUi(Tool tool)
    {
        var meta = tool.Meta;
        if (meta is null)
            return null;

        var ui = meta["ui"] as JsonObject;
        var uri = (ui?["resourceUri"] ?? meta["ui/resourceUri"]) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var visibility = ui?["visibility"] is JsonArray array
            ? array.Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var x) ? x : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : null;

        if (uri is null && visibility is null)
            return null;

        // Default visibility is both.
        var model = visibility is null || visibility.Contains("model");
        var app = visibility is null || visibility.Contains("app");
        return new McpAppToolUi(uri, model, app);
    }

    /// <summary>Reads a UI resource's HTML and its <c>_meta.ui</c> (CSP, border preference).</summary>
    public static McpAppResource? ParseResource(ReadResourceResult result)
    {
        var contents = result.Contents.FirstOrDefault(c => c is TextResourceContents or BlobResourceContents);
        var html = contents switch
        {
            TextResourceContents text => text.Text,
            BlobResourceContents blob => System.Text.Encoding.UTF8.GetString(blob.DecodedData.Span),
            _ => null,
        };
        if (html is null)
            return null;

        var ui = contents!.Meta?["ui"] as JsonObject;
        var csp = ui?["csp"] as JsonObject;
        return new McpAppResource(
            html,
            BuildCsp(Domains(csp, "connectDomains"), Domains(csp, "resourceDomains"), Domains(csp, "frameDomains"), Domains(csp, "baseUriDomains")),
            ui?["prefersBorder"] is JsonValue pb && pb.TryGetValue<bool>(out var border) && border);
    }

    /// <summary>
    /// The view's Content-Security-Policy: the spec's restrictive defaults, opened up only for the
    /// origins the resource declares. Declared values that aren't plain origins are dropped, so a
    /// server can't smuggle extra directives in.
    /// </summary>
    public static string BuildCsp(IReadOnlyList<string> connect, IReadOnlyList<string> resource, IReadOnlyList<string> frame, IReadOnlyList<string> baseUri)
    {
        static string Join(string first, IReadOnlyList<string> extra) => extra.Count == 0 ? first : first + " " + string.Join(' ', extra);

        return string.Join("; ",
            "default-src 'none'",
            Join("script-src 'self' 'unsafe-inline'", resource),
            Join("style-src 'self' 'unsafe-inline'", resource),
            Join("img-src 'self' data: blob:", resource),
            Join("font-src 'self' data:", resource),
            Join("media-src 'self' data: blob:", resource),
            connect.Count == 0 ? "connect-src 'none'" : "connect-src " + string.Join(' ', connect),
            frame.Count == 0 ? "frame-src 'none'" : "frame-src " + string.Join(' ', frame),
            Join("base-uri 'self'", baseUri),
            "form-action 'none'");
    }

    private static List<string> Domains(JsonObject? csp, string key) =>
        csp?[key] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                .Where(s => s is not null && CspSource().IsMatch(s)).Select(s => s!).Distinct().ToList()
            : [];

    // An origin such as https://cdn.example.com, https://*.example.com or wss://api.example.com:8443.
    [GeneratedRegex(@"^(https?|wss?)://(\*\.)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*(:\d{1,5})?/?$")]
    private static partial Regex CspSource();
}

/// <param name="ResourceUri">The <c>ui://</c> resource with the view, if any.</param>
/// <param name="VisibleToModel">Offer the tool to the model (visibility includes "model").</param>
/// <param name="VisibleToApp">The view may call the tool through the host (visibility includes "app").</param>
public sealed record McpAppToolUi(string? ResourceUri, bool VisibleToModel, bool VisibleToApp);

/// <summary>A model-visible tool whose result is shown with an MCP App view.</summary>
public sealed record McpAppTool(string ServerName, Tool Definition, string ResourceUri);

/// <param name="Csp">The Content-Security-Policy to serve the view with.</param>
public sealed record McpAppResource(string Html, string Csp, bool PrefersBorder);

/// <summary>What the view's host (the chat UI) provides to an <see cref="McpAppSession"/>.</summary>
public interface IMcpAppHost
{
    /// <summary>Sends one JSON-RPC message to the view.</summary>
    Task PostToViewAsync(string viewId, string json);

    /// <summary>Adds a message to the chat as if the user had sent it; false if that isn't possible right now.</summary>
    Task<bool> SendUserMessageAsync(string text);

    void OpenLink(string url);

    void SetViewHeight(string viewId, double height);

    /// <summary>Asks the user whether the view may run a tool that changes things (see <see cref="McpHost.CallToolForAppAsync"/>).</summary>
    Task<bool> ApproveToolCallAsync(string serverName, string toolName, IReadOnlyDictionary<string, object?> arguments);

    /// <summary>"light" or "dark".</summary>
    string Theme { get; }
}

/// <summary>
/// The host side of one rendered MCP App view: answers its JSON-RPC requests (ui/initialize,
/// tools/call, resources/read, ui/message, ui/open-link …) and, once it has initialized, sends it the
/// tool's input and result. Tool calls and resource reads are proxied to the view's own server only,
/// and only to tools whose visibility includes "app".
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class McpAppSession
{
    private readonly McpHost _mcp;
    private readonly IMcpAppHost _host;
    private readonly McpAppTool _tool;
    private readonly string _callId;
    private readonly JsonObject _toolInput;
    private readonly JsonElement _toolResult;
    private bool _sentToolData;

    public McpAppSession(string viewId, McpHost mcp, IMcpAppHost host, McpAppTool tool, string callId,
        IDictionary<string, object?>? arguments, JsonElement toolResult)
    {
        ViewId = viewId;
        _mcp = mcp;
        _host = host;
        _tool = tool;
        _callId = callId;
        _toolInput = JsonSerializer.SerializeToNode(arguments ?? new Dictionary<string, object?>()) as JsonObject ?? [];
        _toolResult = toolResult;
    }

    public string ViewId { get; }

    public string ServerName => _tool.ServerName;

    /// <summary>Handles one message from the view. Malformed messages are ignored.</summary>
    public async Task HandleAsync(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var methodElement))
            return; // a response to one of our requests (e.g. ui/resource-teardown) — nothing to do

        var method = methodElement.GetString() ?? string.Empty;
        var hasId = message.TryGetProperty("id", out var id);
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        if (!hasId)
        {
            await HandleNotificationAsync(method, parameters).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await HandleRequestAsync(method, parameters).ConfigureAwait(false);
            await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var code = ex is McpAppMethodNotFoundException ? -32601 : ex is ArgumentException ? -32602 : -32603;
            await PostAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Asks the view to clean up before it is removed (best effort; the reply isn't awaited).</summary>
    public Task TeardownAsync(string reason) =>
        PostAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "teardown-" + ViewId,
            ["method"] = "ui/resource-teardown",
            ["params"] = new JsonObject { ["reason"] = reason },
        });

    public Task NotifyThemeChangedAsync() =>
        NotifyAsync("ui/notifications/host-context-changed", new JsonObject { ["theme"] = _host.Theme });

    private async Task HandleNotificationAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "ui/notifications/initialized":
                if (_sentToolData)
                    return;
                _sentToolData = true;
                await NotifyAsync("ui/notifications/tool-input", new JsonObject { ["arguments"] = _toolInput.DeepClone() }).ConfigureAwait(false);
                await NotifyAsync("ui/notifications/tool-result", JsonNode.Parse(_toolResult.GetRawText())).ConfigureAwait(false);
                break;
            case "ui/notifications/size-changed":
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("height", out var h) && h.TryGetDouble(out var height))
                    _host.SetViewHeight(ViewId, height);
                break;
            // notifications/message (logging) and anything else: nothing to show for now.
        }
    }

    private async Task<JsonNode?> HandleRequestAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "ui/initialize":
                return InitializeResult();

            case "ping":
                return new JsonObject();

            case "ui/update-model-context":
                // Each update replaces the last; the host adds it to the user's next message (see TakeModelContext).
                _modelContext = ModelContextText(parameters);
                return new JsonObject();

            case "ui/request-display-mode":
                return new JsonObject { ["mode"] = "inline" };

            case "ui/open-link":
            {
                var url = RequiredString(parameters, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new ArgumentException("Only http(s) links can be opened.");
                _host.OpenLink(uri.ToString());
                return new JsonObject();
            }

            case "ui/message":
            {
                var text = MessageText(parameters);
                if (string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException("ui/message needs text content.");
                if (!await _host.SendUserMessageAsync(text).ConfigureAwait(false))
                    throw new InvalidOperationException("The chat is busy; try again when the current reply has finished.");
                return new JsonObject();
            }

            case "tools/call":
            {
                var name = RequiredString(parameters, "name");
                var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(a.GetRawText())
                    : null;
                var result = await _mcp.CallToolForAppAsync(
                    _tool.ServerName, name, arguments,
                    args => _host.ApproveToolCallAsync(_tool.ServerName, name, args)).ConfigureAwait(false);
                return JsonNode.Parse(result.GetRawText());
            }

            case "resources/read":
            {
                var uri = RequiredString(parameters, "uri");
                var result = await _mcp.ReadResourceForAppAsync(_tool.ServerName, uri).ConfigureAwait(false);
                return JsonNode.Parse(result.GetRawText());
            }

            default:
                throw new McpAppMethodNotFoundException(method);
        }
    }

    private JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = McpApps.ProtocolVersion,
        ["hostInfo"] = new JsonObject { ["name"] = "Harness.WinUI", ["version"] = typeof(McpApps).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
        ["hostCapabilities"] = new JsonObject
        {
            ["openLinks"] = new JsonObject(),
            ["serverTools"] = new JsonObject(),
            ["serverResources"] = new JsonObject(),
            ["logging"] = new JsonObject(),
            ["message"] = new JsonObject { ["text"] = new JsonObject() },
            ["updateModelContext"] = new JsonObject { ["text"] = new JsonObject(), ["structuredContent"] = new JsonObject() },
        },
        ["hostContext"] = new JsonObject
        {
            ["toolInfo"] = new JsonObject
            {
                ["id"] = _callId,
                ["tool"] = JsonSerializer.SerializeToNode(_tool.Definition, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
            },
            ["theme"] = _host.Theme,
            ["displayMode"] = "inline",
            ["availableDisplayModes"] = new JsonArray("inline"),
            ["locale"] = CultureInfo.CurrentUICulture.Name,
            ["timeZone"] = TimeZoneInfo.Local.Id,
            ["userAgent"] = "Harness.WinUI",
            ["platform"] = "desktop",
            ["deviceCapabilities"] = new JsonObject { ["touch"] = false, ["hover"] = true },
        },
    };

    /// <summary>
    /// The context the view last sent with ui/update-model-context, then cleared: the host adds it to the
    /// user's next message, so the model learns what the user did in the view (e.g. which rows they selected).
    /// </summary>
    public string? TakeModelContext()
    {
        var context = _modelContext;
        _modelContext = null;
        return context;
    }

    private string? _modelContext;

    /// <summary>ui/update-model-context params as text: the text blocks, then structuredContent as JSON. Null when empty.</summary>
    private static string? ModelContextText(JsonElement parameters)
    {
        var text = MessageText(parameters);
        var structured = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("structuredContent", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.GetRawText()
            : null;
        var parts = new[] { text, structured }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>Text from ui/message content: an array of content blocks (SDK) or a single block (spec text).</summary>
    private static string? MessageText(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("content", out var content))
            return null;

        IEnumerable<JsonElement> blocks = content.ValueKind switch
        {
            JsonValueKind.Array => content.EnumerateArray(),
            JsonValueKind.Object => [content],
            _ => [],
        };
        var texts = blocks
            .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out _))
            .Select(b => b.GetProperty("text").GetString())
            .Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", texts);
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException($"Missing \"{name}\".");

    private Task NotifyAsync(string method, JsonNode? parameters) =>
        PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private Task PostAsync(JsonObject message) => _host.PostToViewAsync(ViewId, message.ToJsonString());
}

public sealed class McpAppMethodNotFoundException(string method) : Exception($"Method not found: {method}");
