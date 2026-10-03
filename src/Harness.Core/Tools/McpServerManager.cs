// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Harness.Core.Config;

namespace Harness.Core.Tools;

/// <summary>
/// Add / remove / enable MCP servers: writes mcp.json, keeps header secrets in <see cref="SecretStore"/>,
/// and applies the change to the running <see cref="McpHost"/> immediately. Used by both the MCP
/// settings UI and the agent's own management tools, so both go through the same validation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class McpServerManager
{
    public const string BuiltInServerName = "harness";

    /// <summary>Adds or replaces a Streamable HTTP server. <paramref name="headerValue"/> is stored encrypted.</summary>
    public static async Task<McpServerStatus> AddHttpServerAsync(
        McpHost host, string name, string url, string? headerName, string? headerValue, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (!McpConfig.IsValidName(name))
            throw new ArgumentException("Name must be 1-40 characters: letters, digits, '-' or '_'.", nameof(name));

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps ||
              (uri.Scheme == Uri.UriSchemeHttp && await IsLocalOrPrivateHostAsync(uri, cancellationToken).ConfigureAwait(false))))
        {
            throw new ArgumentException(
                "URL must be https://. Plain http:// is only allowed for servers on this PC or the internal network " +
                "(the host must resolve to a loopback or private address).", nameof(url));
        }

        SecretStore.RemoveByPrefix(SecretPrefix(name));

        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            var header = string.IsNullOrWhiteSpace(headerName) ? "Authorization" : headerName.Trim();
            var secretName = SecretPrefix(name) + header;
            SecretStore.Set(secretName, headerValue.Trim());
            headers = new() { [header] = SecretStore.Reference(secretName) };
        }

        var config = new McpServerConfig { Type = "http", Url = uri.ToString(), Headers = headers };
        McpConfig.Upsert(name, config);
        return await host.ConnectAsync(name, config, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> RemoveAsync(McpHost host, string name)
    {
        var removed = McpConfig.Remove(name);
        SecretStore.RemoveByPrefix(SecretPrefix(name));
        return await host.RemoveAsync(name).ConfigureAwait(false) || removed;
    }

    public static async Task<McpServerStatus?> SetEnabledAsync(McpHost host, string name, bool enabled)
    {
        McpConfig.SetDisabled(name, !enabled);
        return McpConfig.Load().TryGetValue(name, out var config)
            ? await host.ConnectAsync(name, config).ConfigureAwait(false)
            : null;
    }

    private const string FilesystemPackage = "@modelcontextprotocol/server-filesystem";

    /// <summary>
    /// One-time migration to the built-in file tools: removes the Node filesystem MCP server from
    /// mcp.json (so it no longer starts) and returns the first folder it was allowed to access.
    /// </summary>
    public static string? TakeOverFilesystemServer()
    {
        foreach (var (name, config) in McpConfig.Load())
        {
            var args = config.Args ?? [];
            var packageIndex = args.FindIndex(a => a.StartsWith(FilesystemPackage, StringComparison.OrdinalIgnoreCase));
            if (packageIndex < 0)
                continue;

            McpConfig.Remove(name);
            return args.Skip(packageIndex + 1).FirstOrDefault();
        }

        return null;
    }

    /// <summary>Re-reads mcp.json and reconnects every server (after the user edited the file by hand).</summary>
    public static async Task ReloadAsync(McpHost host)
    {
        var servers = McpConfig.Load();
        foreach (var gone in host.Servers.Select(s => s.Name).Where(n => !servers.ContainsKey(n)).ToList())
            await host.RemoveAsync(gone).ConfigureAwait(false);
        await host.StartAsync(servers).ConfigureAwait(false);
    }

    /// <summary>Tools that let the agent manage MCP servers itself. Changes always require approval.</summary>
    public static IEnumerable<AITool> CreateAgentTools(McpHost host, IToolApprover approver, ToolPermissionStore permissions)
    {
        var list = AIFunctionFactory.Create(
            () => host.Servers.Select(s => new { name = s.Name, state = s.State.ToString(), tools = s.ToolCount, error = s.Error, target = s.Summary }),
            "list_mcp_servers",
            "List the MCP servers configured in Harness.WinUI, whether each is connected, and how many tools it provides.");

        var add = AIFunctionFactory.Create(
            async (
                [Description("Short identifier for the server: letters, digits, '-' or '_' (max 40).")] string name,
                [Description("The server's Streamable HTTP endpoint URL: https://..., or http://... for a server on this PC or the internal network.")] string url,
                [Description("Optional auth header name, e.g. \"Authorization\" or \"X-API-Key\". Defaults to Authorization when a value is given.")] string? auth_header_name,
                [Description("Optional auth header value, e.g. \"Bearer <token>\". Stored encrypted.")] string? auth_header_value,
                CancellationToken cancellationToken) =>
            {
                var status = await AddHttpServerAsync(host, name, url, auth_header_name, auth_header_value, cancellationToken).ConfigureAwait(false);
                return status.State == McpServerState.Connected
                    ? $"Connected '{status.Name}' with {status.ToolCount} tools. They are available from the user's next message."
                    : $"Saved '{status.Name}' but it failed to connect: {status.Error}";
            },
            "add_mcp_server",
            "Add (or replace) a remote MCP server in Harness.WinUI using Streamable HTTP, then connect to it. Only do this when the user asks.");

        var remove = AIFunctionFactory.Create(
            async ([Description("Name of the MCP server to remove.")] string name) =>
                await RemoveAsync(host, name).ConfigureAwait(false) ? $"Removed '{name}'." : $"No MCP server named '{name}'.",
            "remove_mcp_server",
            "Remove an MCP server from Harness.WinUI and disconnect it. Only do this when the user asks.");

        return
        [
            new ApprovalGatedFunction(list, BuiltInServerName, list.Name, readOnly: true, approver, permissions),
            new ApprovalGatedFunction(add, BuiltInServerName, add.Name, readOnly: false, approver, permissions, canAlwaysAllow: false),
            new ApprovalGatedFunction(remove, BuiltInServerName, remove.Name, readOnly: false, approver, permissions, canAlwaysAllow: false),
        ];
    }

    private static string SecretPrefix(string serverName) => $"mcp/{serverName}/";

    /// <summary>
    /// True when every address the host resolves to is loopback or private (RFC 1918, link-local, IPv6 ULA):
    /// plain http is fine inside the internal network, but an auth header must not cross the internet in clear text.
    /// </summary>
    private static async Task<bool> IsLocalOrPrivateHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsLoopback)
            return true;
        try
        {
            var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsLocalOrPrivate);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static bool IsLocalOrPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10                                  // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254);               // 169.254.0.0/16 link-local
    }
}
