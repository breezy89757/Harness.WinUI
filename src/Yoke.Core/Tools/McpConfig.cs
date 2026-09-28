// Yoke — Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Yoke.Core.Tools;

/// <summary>
/// One entry under <c>"mcpServers"</c> in mcp.json — the same shape Claude Desktop, VS Code and
/// Cursor use, so existing server configs can be pasted in as-is. A server is stdio when
/// <see cref="Command"/> is set, remote (Streamable HTTP / SSE) when <see cref="Url"/> is set.
/// Header and env values may contain <c>${secret:name}</c> references (see Config.SecretStore).
/// </summary>
public sealed record McpServerConfig
{
    public string? Command { get; init; }
    public List<string>? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public string? Cwd { get; init; }

    /// <summary>"http", "streamable-http" or "sse"; anything else auto-detects.</summary>
    public string? Type { get; init; }
    public string? Url { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    public bool Disabled { get; init; }

    /// <summary>Fields other clients use (e.g. "autoApprove") — kept so saving doesn't drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public string Summary => Url ?? string.Join(' ', new[] { Command }.Concat(Args ?? []).Where(s => !string.IsNullOrEmpty(s)));
}

public static partial class McpConfig
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke", "mcp.json");

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lock s_lock = new();

    /// <summary>Server names become part of tool names and secret keys, so keep them simple.</summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>Returns the configured servers (empty if the file doesn't exist). Throws on malformed JSON.</summary>
    public static IReadOnlyDictionary<string, McpServerConfig> Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new Dictionary<string, McpServerConfig>();

        var file = JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path), s_json);
        return file?.McpServers ?? new Dictionary<string, McpServerConfig>();
    }

    /// <summary>Adds or replaces one server and saves. Other top-level keys in the file are kept (comments are not).</summary>
    public static void Upsert(string name, McpServerConfig server, string? path = null) =>
        Update(path, servers => servers[name] = JsonSerializer.SerializeToNode(server, s_json));

    public static bool Remove(string name, string? path = null)
    {
        var removed = false;
        Update(path, servers => removed = servers.Remove(name));
        return removed;
    }

    public static void SetDisabled(string name, bool disabled, string? path = null)
    {
        var servers = Load(path);
        if (servers.TryGetValue(name, out var server))
            Upsert(name, server with { Disabled = disabled }, path);
    }

    private static void Update(string? path, Action<JsonObject> change)
    {
        path ??= DefaultPath;
        lock (s_lock)
        {
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : null;
            root ??= new JsonObject();

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            change(servers);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(s_json));
        }
    }

    /// <summary>Creates an empty mcp.json with a commented example if none exists, and returns its path.</summary>
    public static string EnsureExists(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              // Same format as Claude Desktop / VS Code / Cursor.
              // Example (stdio):
              //   "filesystem": {
              //     "command": "npx",
              //     "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\you\\Documents"]
              //   }
              // Example (remote):
              //   "my-remote": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ..." } }
              "mcpServers": {
              }
            }
            """);
        return path;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ValidName();

    private sealed record McpConfigFile
    {
        public Dictionary<string, McpServerConfig>? McpServers { get; init; }
    }
}
