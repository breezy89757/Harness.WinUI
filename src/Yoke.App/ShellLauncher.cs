// Yoke — Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using Windows.System;
using Yoke.Core.Tools;
using Yoke.MarkdownRendering;

namespace Yoke.App;

/// <summary>
/// Everything that hands a link or file to Windows. Opening is best effort: a missing file
/// association must not crash the app from an event handler, so failures are swallowed.
/// </summary>
public static class ShellLauncher
{
    // Formats that only get *viewed* by their default app (Office formats here are the macro-free ones;
    // .docm/.xlsm are not listed). Anything else (exe, bat, ps1, lnk, html…)
    // is revealed in Explorer instead of opened, so a model-written link can never run a program.
    private static readonly HashSet<string> s_previewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".pdf", ".txt", ".md", ".csv", ".json", ".log",
        ".docx", ".xlsx", ".pptx",
    };

    /// <summary>Opens a link from chat or artifact content: web links in the browser, local files safely.</summary>
    public static void OpenLink(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
            return;

        // Generated images are served from a virtual host; open the real file in the default viewer.
        if (target.Host.Equals(ChatShell.ImagesHostName, StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.Combine(ImageGenerationTool.OutputDirectory, Path.GetFileName(Uri.UnescapeDataString(target.AbsolutePath)));
            if (File.Exists(file))
                OpenFile(file);
            return;
        }

        if (target.IsFile)
            OpenLocalPath(target.LocalPath);
        else if (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeMailto)
            _ = Launcher.LaunchUriAsync(target);
    }

    public static void OpenLocalPath(string path)
    {
        if (Directory.Exists(path))
            Start("explorer.exe", $"\"{path}\"");
        else if (File.Exists(path) && s_previewableExtensions.Contains(Path.GetExtension(path)))
            OpenFile(path);
        else if (File.Exists(path))
            Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>Opens a file the app itself wrote (config, artifact, image) in its default app, falling back to Notepad.</summary>
    public static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Start("notepad.exe", $"\"{path}\""); // no app associated with this extension
        }
    }

    private static void Start(string fileName, string arguments)
    {
        try
        {
            Process.Start(fileName, arguments);
        }
        catch (Win32Exception)
        {
        }
    }
}
