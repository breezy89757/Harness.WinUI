// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Files;

/// <summary>
/// The one folder the file tools may touch. Every path from the model goes through <see cref="Resolve"/>:
/// relative paths (and "/x", which models often mean as sandbox-relative) are anchored at the root,
/// anything that normalizes outside it is rejected, and symlinks/junctions along the way must not
/// lead outside either.
/// </summary>
public sealed class Sandbox
{
    public Sandbox(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string Resolve(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        string candidate;
        if (trimmed is "" or "." or "/" or "\\")
            candidate = Root;
        else if (Path.IsPathFullyQualified(trimmed))
            candidate = Path.GetFullPath(trimmed);
        else
            candidate = Path.GetFullPath(Path.Combine(Root, trimmed.TrimStart('/', '\\')));

        if (!IsInside(candidate))
            throw new UnauthorizedAccessException($"'{path}' is outside the sandbox folder. Use paths relative to the sandbox root.");

        // A link inside the sandbox must not be a way out: check every existing component.
        for (var probe = candidate; probe is not null && probe.Length > Root.Length; probe = Path.GetDirectoryName(probe))
        {
            FileSystemInfo? info = Directory.Exists(probe) ? new DirectoryInfo(probe) : File.Exists(probe) ? new FileInfo(probe) : null;
            if (info?.LinkTarget is null)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !IsInside(Path.GetFullPath(target)))
                throw new UnauthorizedAccessException($"'{path}' goes through a link that points outside the sandbox folder.");
        }

        return candidate;
    }

    /// <summary>Path as shown to the model: relative to the root, with forward slashes.</summary>
    public string Relative(string fullPath)
    {
        var relative = Path.GetRelativePath(Root, fullPath);
        return relative == "." ? "." : relative.Replace('\\', '/');
    }

    private bool IsInside(string fullPath) =>
        fullPath.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
