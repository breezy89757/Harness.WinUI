// Yoke — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Yoke.Core.Config;

namespace Yoke.Core.Tools;

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
                new McpClientOptions { ClientInfo = new Implementation { Name = "Harness.WinUI", Version = "1.0.0" } },
                cancellationToken: timeout.Token).ConfigureAwait(false);

            tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            status = new McpServerStatus(name, McpServerState.Connected, tools.Count, null, summary);
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
        if (_approver is { } approver && _permissions is { } permissions)
        {
            var exposedNames = new HashSet<string>(StringComparer.Ordinal);
            List<(string Server, McpClientTool Tool)> all;
            lock (_lock)
                all = _entries.SelectMany(e => e.Value.Tools.Select(t => (e.Key, t))).ToList();

            foreach (var (server, tool) in all)
            {
                // OpenAI-style function names allow only [A-Za-z0-9_-]{1,64}; on a clash between
                // servers, the later one gets the server name as a prefix.
                var exposed = SanitizeName(tool.Name);
                if (!exposedNames.Add(exposed))
                {
                    exposed = SanitizeName($"{server}_{tool.Name}");
                    exposedNames.Add(exposed);
                }

                AIFunction function = exposed == tool.Name ? tool : tool.WithName(exposed);
                var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true;
                tools.Add(new ApprovalGatedFunction(function, server, tool.Name, readOnly, approver, permissions));
            }
        }

        Volatile.Write(ref _tools, tools);
        Changed?.Invoke(this, EventArgs.Empty);
    }

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
