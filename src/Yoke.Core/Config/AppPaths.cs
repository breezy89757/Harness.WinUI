// Yoke — Licensed under the MIT License.

namespace Yoke.Core.Config;

/// <summary>
/// Where Yoke keeps its files. Unpackaged, that's %LOCALAPPDATA%\Yoke. As an MSIX package (Microsoft
/// Store) the app sets it to the package's LocalState folder at startup: a packaged app's writes to
/// %LOCALAPPDATA% are redirected to a private location other programs can't see at the original path,
/// and Yoke hands files to other programs (a created document to Word, an artifact to the browser,
/// mcp.json to an editor). LocalState is a real folder they can open.
/// </summary>
public static class AppPaths
{
    public static string DataRoot { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke");

    /// <summary>Call once at startup, before anything reads or writes app data.</summary>
    public static void UseDataRoot(string folder) => DataRoot = folder;

    /// <summary>A file or folder directly under <see cref="DataRoot"/>.</summary>
    public static string Combine(string name) => Path.Combine(DataRoot, name);
}
