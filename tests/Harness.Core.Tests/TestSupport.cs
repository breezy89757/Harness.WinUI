// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Tools;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tests;

/// <summary>A folder that is removed when the test is over.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public string Read(string relative) => File.ReadAllText(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public bool Exists(string relative) => File.Exists(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Answers every question with the same decision and counts the questions.</summary>
public sealed class CountingApprover(ToolApprovalDecision decision = ToolApprovalDecision.AllowOnce) : IToolApprover
{
    public int Asked { get; private set; }

    public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        Asked++;
        return Task.FromResult(decision);
    }
}

public static class ToolExtensions
{
    public static async Task<string> CallAsync(this IEnumerable<AITool> tools, string name, Dictionary<string, object?>? arguments = null)
    {
        var tool = tools.OfType<AIFunction>().First(t => t.Name == name);
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments ?? []));
        return result?.ToString() ?? string.Empty;
    }

    public static ToolPermissionStore NewPermissions(this TempDir dir) => new(System.IO.Path.Combine(dir.Path, "permissions.json"));
}
