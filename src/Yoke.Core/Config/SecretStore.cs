// Yoke — Licensed under the MIT License.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yoke.Core.Config;

/// <summary>
/// Named secrets (e.g. an MCP server's Authorization header), each DPAPI-encrypted for the current
/// Windows user in %LOCALAPPDATA%\Yoke\secrets.json. Config files refer to them as
/// <c>${secret:name}</c> so they never hold the plaintext value.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class SecretStore
{
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("Yoke.Secret.v1");
    private static readonly Lock s_lock = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke", "secrets.json");

    public static string Reference(string name) => "${secret:" + name + "}";

    public static void Set(string name, string value)
    {
        lock (s_lock)
        {
            var all = ReadAll();
            all[name] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), s_entropy, DataProtectionScope.CurrentUser));
            WriteAll(all);
        }
    }

    public static void RemoveByPrefix(string prefix)
    {
        lock (s_lock)
        {
            var all = ReadAll();
            if (all.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList() is { Count: > 0 } keys)
            {
                keys.ForEach(k => all.Remove(k));
                WriteAll(all);
            }
        }
    }

    /// <summary>Replaces every <c>${secret:name}</c> in <paramref name="value"/>; throws if a referenced secret is missing.</summary>
    public static string Resolve(string value)
    {
        if (!value.Contains("${secret:", StringComparison.Ordinal))
            return value;

        Dictionary<string, string> all;
        lock (s_lock)
            all = ReadAll();

        return SecretReference().Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            if (!all.TryGetValue(name, out var protectedValue))
                throw new InvalidOperationException($"Secret '{name}' is referenced in config but not stored on this machine.");
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), s_entropy, DataProtectionScope.CurrentUser));
        });
    }

    private static Dictionary<string, string> ReadAll()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WriteAll(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    [GeneratedRegex(@"\$\{secret:([^}]+)\}")]
    private static partial Regex SecretReference();
}
