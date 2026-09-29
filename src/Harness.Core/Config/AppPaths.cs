// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Config;

/// <summary>
/// Where Harness.WinUI keeps its files. Unpackaged, that's %LOCALAPPDATA%\Harness.WinUI. As an MSIX package (Microsoft
/// Store) the app sets it to the package's LocalState folder at startup: a packaged app's writes to
/// %LOCALAPPDATA% are redirected to a private location other programs can't see at the original path,
/// and Harness.WinUI hands files to other programs (a created document to Word, an artifact to the browser,
/// mcp.json to an editor). LocalState is a real folder they can open.
/// </summary>
public static class AppPaths
{
    private static readonly string s_localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataRoot { get; private set; } = Path.Combine(s_localAppData, "Harness.WinUI");

    /// <summary>The unpackaged data folder used before the app was renamed (1.0.0 and earlier).</summary>
    public static string LegacyDataRoot { get; } = Path.Combine(s_localAppData, "Yoke");

    /// <summary>Call once at startup, before anything reads or writes app data.</summary>
    public static void UseDataRoot(string folder) => DataRoot = folder;

    /// <summary>
    /// Unpackaged only, once at startup: moves the pre-rename data folder (%LOCALAPPDATA%\Yoke) to
    /// <see cref="DataRoot"/> if there's nothing there yet, so settings, keys and history carry over.
    /// </summary>
    public static void MigrateLegacyDataRoot()
    {
        try
        {
            if (Directory.Exists(LegacyDataRoot) && !Directory.Exists(DataRoot))
                Directory.Move(LegacyDataRoot, DataRoot);
        }
        catch (IOException)
        {
            // In use or on another volume: keep going with a fresh folder rather than failing to start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A path saved under <see cref="LegacyDataRoot"/> (e.g. the default sandbox) mapped to the same place
    /// under <see cref="DataRoot"/>; any other path unchanged.
    /// </summary>
    public static string? MapLegacyPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var legacy = LegacyDataRoot + Path.DirectorySeparatorChar;
        if (path.Equals(LegacyDataRoot, StringComparison.OrdinalIgnoreCase))
            return DataRoot;
        return path.StartsWith(legacy, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(DataRoot, path[legacy.Length..])
            : path;
    }

    /// <summary>A file or folder directly under <see cref="DataRoot"/>.</summary>
    public static string Combine(string name) => Path.Combine(DataRoot, name);
}
