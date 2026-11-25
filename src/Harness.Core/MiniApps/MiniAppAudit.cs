// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using Harness.Core.Config;

namespace Harness.Core.MiniApps;

/// <summary>
/// Every call a Mini App makes through the bridge, one JSON line each in <c>apps\audit.jsonl</c> (its own file: the observability
/// records only exist while recording is on). What the app asked is summarised, never copied: no message text, only counts.
/// </summary>
public sealed class MiniAppAudit(string? path = null)
{
    private const long MaxBytes = 2_000_000;
    private static readonly object s_gate = new();

    public string Path { get; } = path ?? AppPaths.Combine(System.IO.Path.Combine("apps", "audit.jsonl"));

    public void Write(MiniAppInfo app, string method, string summary, bool ok, string? error = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            appId = app.Id,
            name = app.Name,
            version = app.VersionHash,
            method,
            summary,
            ok,
            error,
        });

        try
        {
            lock (s_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Kept small: when it grows past the limit the older half is dropped.
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines[(lines.Length / 2)..]);
                }

                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An audit that can't be written must not break the app's call.
        }
    }
}
