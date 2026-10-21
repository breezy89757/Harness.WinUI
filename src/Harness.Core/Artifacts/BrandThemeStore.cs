// Harness.WinUI — Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Config;

namespace Harness.Core.Artifacts;

/// <summary>
/// The company themes the person made from their templates, one folder each under the themes folder (the data folder, see
/// <see cref="AppPaths"/>): theme.json, a copy of the template and its pictures. Nothing here is part of the program, so a company's
/// brand never ends up in its source code.
/// </summary>
public sealed partial class BrandThemeStore(string? root = null)
{
    public const string ThemeFile = "theme.json";

    private readonly string _root = root ?? AppPaths.Combine("themes");

    /// <summary>The theme's folder. An id that isn't a plain folder name (a changed preferences file) is a folder that does not exist.</summary>
    public string FolderOf(string id) => Plain().IsMatch(id) ? Path.Combine(_root, id) : Path.Combine(_root, ".none");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,63}$")]
    private static partial Regex Plain();

    /// <summary>The built-in theme first, then the person's.</summary>
    public IReadOnlyList<BrandTheme> List()
    {
        var themes = new List<BrandTheme> { BrandTheme.BuiltIn };
        if (!Directory.Exists(_root))
            return themes;

        foreach (var folder in Directory.EnumerateDirectories(_root).Order(StringComparer.OrdinalIgnoreCase))
        {
            // Folders that are being made or were left by a failed replacement start with a dot.
            if (Plain().IsMatch(Path.GetFileName(folder)) && Load(folder) is { } theme)
                themes.Add(theme);
        }

        return themes;
    }

    /// <summary>The theme with this id, or the built-in one (a theme that was deleted, or none chosen).</summary>
    public BrandTheme Resolve(string? id) =>
        string.IsNullOrEmpty(id) || id == BrandTheme.DefaultId ? BrandTheme.BuiltIn
        : Load(FolderOf(id)) ?? BrandTheme.BuiltIn;

    /// <summary>The template file of a company theme, or null for the built-in one.</summary>
    public string? TemplatePathOf(BrandTheme theme) =>
        theme.Template is { } file && Path.Combine(FolderOf(theme.Id), file) is var path && File.Exists(path) ? path : null;

    public const string RulesFile = "design.md";

    /// <summary>
    /// Where the design guide of a theme is: next to the theme's folder, not in it, so deleting a theme or making it again from a new copy of
    /// the template keeps what the person wrote (a theme of the same name finds it again).
    /// </summary>
    public string RulesPathOf(string id) => Path.Combine(_root, id + ".design.md");

    /// <summary>The design guide of a theme (what the person or the analysis wrote), or null when it has none.</summary>
    public string? ReadRules(string id)
    {
        try
        {
            // Guides made before they were kept apart were in the theme's folder.
            var inFolder = Path.Combine(FolderOf(id), RulesFile);
            if (!File.Exists(RulesPathOf(id)) && File.Exists(inFolder))
            {
                Directory.CreateDirectory(_root);
                File.Copy(inFolder, RulesPathOf(id));
            }

            return File.Exists(RulesPathOf(id)) && File.ReadAllText(RulesPathOf(id)).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SaveRules(string id, string markdown)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(RulesPathOf(id), markdown.Trim() + "\n");
    }

    /// <summary>Makes a theme of a .pptx template and keeps it. A theme with the same name is replaced.</summary>
    /// <exception cref="InvalidDataException">The file isn't a usable presentation.</exception>
    public BrandTheme Import(string pptxPath, string name)
    {
        name = name.Trim();
        var id = IdFor(name);
        var folder = FolderOf(id);
        var staging = Path.Combine(_root, ".import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var theme = BrandThemeExtractor.FromPptx(pptxPath, id, name, staging);
            File.WriteAllText(Path.Combine(staging, ThemeFile), theme.ToJson());
            // The old theme of this name stays until the new one is in place.
            var previous = Directory.Exists(folder) ? folder + ".old-" + Guid.NewGuid().ToString("N")[..6] : null;
            if (previous is not null)
                Directory.Move(folder, previous);
            try
            {
                Directory.Move(staging, folder);
            }
            catch
            {
                if (previous is not null && !Directory.Exists(folder))
                    Directory.Move(previous, folder);
                throw;
            }

            if (previous is not null)
                Directory.Delete(previous, recursive: true);
            return theme;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public void Delete(string id)
    {
        if (id != BrandTheme.DefaultId && Directory.Exists(FolderOf(id)))
            Directory.Delete(FolderOf(id), recursive: true);
    }

    /// <summary>Copies the pictures of a theme next to the preview pages (<c>_themes/id/…</c>), where the preview finds them.</summary>
    public void PublishAssets(BrandTheme theme, string previewFolder)
    {
        if (theme.IsBuiltIn)
            return;

        var source = FolderOf(theme.Id);
        var target = Path.Combine(previewFolder, "_themes", theme.Id);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source).Where(f => !f.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
                File.Copy(file, destination);
        }
    }

    private static BrandTheme? Load(string folder)
    {
        var file = Path.Combine(folder, ThemeFile);
        try
        {
            return File.Exists(file) ? BrandTheme.FromJson(File.ReadAllText(file)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A folder name for a theme: its name when that is plain letters and digits, else a short hash of it.</summary>
    private static string IdFor(string name)
    {
        var slug = Unsafe().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length is > 0 and <= 40 && slug != BrandTheme.DefaultId && slug == name.ToLowerInvariant().Replace(' ', '-'))
            return slug;
        return "t" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex Unsafe();
}
