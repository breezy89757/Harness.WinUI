// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Harness.Core.Config;

namespace Harness.Core.Tools;

public enum McpServerState
{
    Starting,
    Connected,
    Failed,
    Disabled,
}

public sealed record McpServerStatus(string Name, McpServerState State, int ToolCount, string? Error, string Summary);

/// <summary>
/// Owns the MCP client connections. Servers connect in parallel, each with its own timeout (so one
/// slow <c>npx</c> download doesn't hold up the rest), and can be (re)connected or removed one at a
/// time while the app runs. Exposes every connected server's tools wrapped in
/// <see cref="ApprovalGatedFunction"/>. Disposing shuts servers down (stdio children are killed).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class McpHost : IAsyncDisposable
{
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(90);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private IToolApprover? _approver;
    private ToolPermissionStore? _permissions;
    private IReadOnlyList<AITool> _tools = [];

    /// <summary>Raised (on a background thread) whenever a server's state or the tool list changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AITool> Tools => Volatile.Read(ref _tools);

    public IReadOnlyList<McpServerStatus> Servers
    {
        get
        {
            lock (_lock)
                return _entries.Values.Select(e => e.Status).ToList();
        }
    }

    /// <summary>Must be called before connecting: tools are gated through this approver.</summary>
    public void Configure(IToolApprover approver, ToolPermissionStore permissions)
    {
        _approver = approver;
        _permissions = permissions;
        RebuildTools();
    }

    public Task StartAsync(IReadOnlyDictionary<string, McpServerConfig> servers, CancellationToken cancellationToken = default) =>
        Task.WhenAll(servers.Select(s => ConnectAsync(s.Key, s.Value, cancellationToken)));

    /// <summary>Connects (or reconnects, replacing any existing connection) one server.</summary>
    public async Task<McpServerStatus> ConnectAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default)
    {
        var summary = config.Summary;
        int generation;
        McpClient? previous;
        lock (_lock)
        {
            _entries.TryGetValue(name, out var existing);
            previous = existing?.Client;
            generation = (existing?.Generation ?? 0) + 1;
            _entries[name] = new Entry(generation, null, [],
                new McpServerStatus(name, config.Disabled ? McpServerState.Disabled : McpServerState.Starting, 0, null, summary));
        }

        await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        RebuildTools();

        if (config.Disabled)
            return Current(name)!;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_startTimeout);

        McpClient? client = null;
        McpServerStatus status;
        IList<McpClientTool> tools = [];
        try
        {
            client = await McpClient.CreateAsync(
                CreateTransport(name, config),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "Harness.WinUI", Version = "1.0.0" },
                    // MCP Apps: tools may come with an HTML view, rendered next to the tool call.
                    Capabilities = new ClientCapabilities
                    {
                        Extensions = new Dictionary<string, object> { [McpApps.ExtensionId] = McpApps.ClientCapability },
                    },
                },
                cancellationToken: timeout.Token).ConfigureAwait(false);

            tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            // Count what the model gets: tools only an MCP App view may call aren't offered to it.
            var modelToolCount = tools.Count(t => McpApps.GetToolUi(t.ProtocolTool) is not { VisibleToModel: false });
            status = new McpServerStatus(name, McpServerState.Connected, modelToolCount, null, summary);
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            client = null;
            var message = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? $"Timed out after {s_startTimeout.TotalSeconds:F0}s while starting."
                : ex.Message;
            status = new McpServerStatus(name, McpServerState.Failed, 0, message, summary);
        }

        bool superseded;
        lock (_lock)
        {
            superseded = !_entries.TryGetValue(name, out var current) || current.Generation != generation;
            if (!superseded)
                _entries[name] = new Entry(generation, client, tools, status);
        }

        if (superseded)
        {
            // Removed or reconnected while we were starting — drop this connection.
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            return status;
        }

        RebuildTools();
        return status;
    }

    public async Task<bool> RemoveAsync(string name)
    {
        Entry? removed;
        lock (_lock)
        {
            if (_entries.Remove(name, out removed) is false)
                return false;
        }

        await DisposeQuietlyAsync(removed.Client).ConfigureAwait(false);
        RebuildTools();
        return true;
    }

    private McpServerStatus? Current(string name)
    {
        lock (_lock)
            return _entries.TryGetValue(name, out var e) ? e.Status : null;
    }

    private void RebuildTools()
    {
        var tools = new List<AITool>();
        var appTools = new Dictionary<string, McpAppTool>(StringComparer.Ordinal);
        if (_approver is { } approver && _permissions is { } permissions)
        {
            var exposedNames = new HashSet<string>(StringComparer.Ordinal);
            List<(string Server, McpClientTool Tool)> all;
            lock (_lock)
                all = _entries.SelectMany(e => e.Value.Tools.Select(t => (e.Key, t))).ToList();

            foreach (var (server, tool) in all)
            {
                // MCP Apps: tools only an app view may call are never offered to the model.
                var ui = McpApps.GetToolUi(tool.ProtocolTool);
                if (ui is { VisibleToModel: false })
                    continue;

                // OpenAI-style function names allow only [A-Za-z0-9_-]{1,64}; on a clash between
                // servers, the later one gets the server name as a prefix.
                var exposed = SanitizeName(tool.Name);
                if (!exposedNames.Add(exposed))
                {
                    exposed = SanitizeName($"{server}_{tool.Name}");
                    exposedNames.Add(exposed);
                }

                AIFunction function = exposed == tool.Name ? tool : tool.WithName(exposed);
                if (ui?.ResourceUri is { } resourceUri)
                {
                    appTools[exposed] = new McpAppTool(server, tool.ProtocolTool, resourceUri);
                    function = new AppToolFunction(function, this);
                }

                var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true;
                tools.Add(new ApprovalGatedFunction(function, server, tool.Name, readOnly, approver, permissions));
            }
        }

        Volatile.Write(ref _appTools, appTools);
        Volatile.Write(ref _tools, tools);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    #region MCP Apps

    private IReadOnlyDictionary<string, McpAppTool> _appTools = new Dictionary<string, McpAppTool>();

    // Full results of app tool calls, by call id, until the chat UI picks them up for the view.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement> _appResults = new();

    /// <summary>The full result (with structuredContent) of an app tool call; null if there is none.</summary>
    public JsonElement? TakeAppResult(string callId) =>
        _appResults.TryRemove(callId, out var result) ? result : null;

    /// <summary>
    /// An MCP App tool as the model sees it. As in MCP Apps hosts like Goose, the model gets the result's
    /// <c>content</c> only: <c>structuredContent</c> (e.g. every row of a query) is for the view, and is
    /// kept for it here instead of filling the model's context.
    /// </summary>
    private sealed class AppToolFunction(AIFunction inner, McpHost host) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (result is not JsonElement { ValueKind: JsonValueKind.Object } full)
                return result;

            if (FunctionInvokingChatClient.CurrentContext?.CallContent.CallId is { } callId)
                host._appResults[callId] = full;

            var forModel = new System.Text.Json.Nodes.JsonObject();
            if (full.TryGetProperty("content", out var content))
                forModel["content"] = System.Text.Json.Nodes.JsonNode.Parse(content.GetRawText());
            if (full.TryGetProperty("isError", out var isError))
                forModel["isError"] = System.Text.Json.Nodes.JsonNode.Parse(isError.GetRawText());
            return JsonSerializer.SerializeToElement(forModel);
        }
    }

    /// <summary>The MCP App view behind a tool, by the name the model called it with; null if it has none.</summary>
    public McpAppTool? FindAppTool(string exposedName) =>
        Volatile.Read(ref _appTools).TryGetValue(exposedName, out var tool) ? tool : null;

    /// <summary>Reads a tool's <c>ui://</c> view from its server.</summary>
    public async Task<McpAppResource?> ReadAppResourceAsync(McpAppTool tool, CancellationToken cancellationToken = default)
    {
        var result = await ClientFor(tool.ServerName).ReadResourceAsync(tool.ResourceUri, cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpApps.ParseResource(result);
    }

    /// <summary>
    /// A <c>tools/call</c> from a view, proxied to the view's own server. Only tools whose visibility
    /// includes "app" may be called. A tool the model can also call goes through the same approval
    /// as a model call unless it is read-only or always-allowed; app-only tools are part of the view.
    /// </summary>
    public async Task<JsonElement> CallToolForAppAsync(
        string serverName, string toolName, IReadOnlyDictionary<string, object?>? arguments,
        Func<IReadOnlyDictionary<string, object?>, Task<bool>> approve, CancellationToken cancellationToken = default)
    {
        McpClient client;
        McpClientTool? tool;
        lock (_lock)
        {
            if (!_entries.TryGetValue(serverName, out var entry) || entry.Client is null)
                throw new InvalidOperationException($"MCP server '{serverName}' is not connected.");
            client = entry.Client;
            tool = entry.Tools.FirstOrDefault(t => t.Name == toolName);
        }

        var ui = tool is null ? null : McpApps.GetToolUi(tool.ProtocolTool);
        if (tool is null || ui is { VisibleToApp: false })
            throw new ArgumentException($"Tool '{toolName}' is not available to this app.");

        arguments ??= new Dictionary<string, object?>();
        var needsApproval = (ui?.VisibleToModel ?? true) &&
            tool.ProtocolTool.Annotations?.ReadOnlyHint != true &&
            !(_permissions?.IsAlwaysAllowed(serverName, toolName) ?? false);
        if (needsApproval && !await approve(arguments).ConfigureAwait(false))
            throw new InvalidOperationException("The user declined to run this tool.");

        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    /// <summary>A <c>resources/read</c> from a view, proxied to the view's own server.</summary>
    public async Task<JsonElement> ReadResourceForAppAsync(string serverName, string uri, CancellationToken cancellationToken = default)
    {
        var result = await ClientFor(serverName).ReadResourceAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    private McpClient ClientFor(string serverName)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(serverName, out var entry) && entry.Client is { } client
                ? client
                : throw new InvalidOperationException($"MCP server '{serverName}' is not connected.");
        }
    }

    #endregion

    private static IClientTransport CreateTransport(string name, McpServerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Command))
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = config.Command,
                Arguments = config.Args ?? [],
                WorkingDirectory = config.Cwd,
                EnvironmentVariables = config.Env?.ToDictionary(e => e.Key, e => (string?)SecretStore.Resolve(e.Value)),
            });
        }

        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(config.Url),
                AdditionalHeaders = config.Headers?.ToDictionary(h => h.Key, h => SecretStore.Resolve(h.Value)),
                TransportMode = config.Type?.ToLowerInvariant() switch
                {
                    "sse" => HttpTransportMode.Sse,
                    "http" or "streamable-http" or "streamablehttp" => HttpTransportMode.StreamableHttp,
                    _ => HttpTransportMode.AutoDetect,
                },
            });
        }

        throw new InvalidOperationException("Server config needs either \"command\" (stdio) or \"url\" (remote).");
    }

    private static async Task DisposeQuietlyAsync(McpClient? client)
    {
        if (client is null)
            return;
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    private static string SanitizeName(string name)
    {
        var sanitized = InvalidNameChars().Replace(name, "_");
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidNameChars();

    public async ValueTask DisposeAsync()
    {
        List<McpClient?> clients;
        lock (_lock)
        {
            clients = _entries.Values.Select(e => e.Client).ToList();
            _entries.Clear();
        }

        foreach (var client in clients)
            await DisposeQuietlyAsync(client).ConfigureAwait(false);

        Volatile.Write(ref _tools, []);
    }

    private sealed record Entry(int Generation, McpClient? Client, IList<McpClientTool> Tools, McpServerStatus Status);
}
