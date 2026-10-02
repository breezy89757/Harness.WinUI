// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using Harness.Core.Config;

namespace Harness.Core.Skills;

/// <summary>A skill folder (Agent Skills format, agentskills.io): SKILL.md with name/description frontmatter plus optional files.</summary>
/// <param name="Source">Where it was found, for the UI (e.g. "Harness.WinUI", "~/.agents/skills").</param>
public sealed record Skill(string Name, string Description, string Directory, string Source);

/// <summary>A SKILL.md that couldn't be used, and why.</summary>
public sealed record SkillProblem(string Path, string Message);

/// <summary>
/// Finds skills in the usual places, the same ones other agents (Goose, Claude Code) read, so a skill
/// written once works everywhere. The model sees each skill's name and description; it loads the full
/// instructions with load_skill when a task calls for one, and supporting files with read_skill_file.
/// </summary>
public static partial class SkillCatalog
{
    /// <summary>Harness.WinUI's own skills folder (created on demand).</summary>
    public static string AppSkillsFolder => AppPaths.Combine("skills");

    /// <summary>Folders searched, in priority order (the first skill with a given name wins).</summary>
    public static IReadOnlyList<(string Folder, string Source)> Roots(string? sandboxRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string, string)>
        {
            (AppSkillsFolder, "Harness.WinUI"),
            (Path.Combine(home, ".agents", "skills"), "~/.agents/skills"),
            (Path.Combine(home, ".claude", "skills"), "~/.claude/skills"),
        };
        if (!string.IsNullOrEmpty(sandboxRoot))
            roots.Add((Path.Combine(sandboxRoot, ".agents", "skills"), "sandbox/.agents/skills"));
        return roots;
    }

    public static (IReadOnlyList<Skill> Skills, IReadOnlyList<SkillProblem> Problems) Discover(string? sandboxRoot)
    {
        var skills = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var problems = new List<SkillProblem>();
        foreach (var (folder, source) in Roots(sandboxRoot))
        {
            if (!System.IO.Directory.Exists(folder))
                continue;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(folder))
            {
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file))
                    continue;
                try
                {
                    var (name, description, _) = Parse(File.ReadAllText(file));
                    var problem = Validate(name, description, Path.GetFileName(directory));
                    if (problem is not null)
                        problems.Add(new SkillProblem(file, problem));
                    else
                        skills.TryAdd(name!, new Skill(name!, description!, directory, source));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
                {
                    problems.Add(new SkillProblem(file, ex.Message));
                }
            }
        }
        return (skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList(), problems);
    }

    /// <summary>Splits SKILL.md into its frontmatter fields and Markdown body.</summary>
    public static (string? Name, string? Description, string Body) Parse(string text)
    {
        var match = Frontmatter().Match(text);
        if (!match.Success)
            return (null, null, text);
        var yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
            .Deserialize<Dictionary<string, object?>>(match.Groups["yaml"].Value) ?? [];
        string? Field(string key) => yaml.TryGetValue(key, out var v) ? v?.ToString()?.Trim() : null;
        return (Field("name"), Field("description"), text[match.Length..].TrimStart('\r', '\n'));
    }

    private static string? Validate(string? name, string? description, string folderName)
    {
        if (string.IsNullOrEmpty(name))
            return "Missing \"name\" in the frontmatter.";
        if (name.Length > 64 || !SkillName().IsMatch(name) || name.Contains("--", StringComparison.Ordinal))
            return $"Invalid name \"{name}\": 1-64 lowercase letters, digits and single hyphens.";
        if (!string.Equals(name, folderName, StringComparison.Ordinal))
            return $"The name \"{name}\" must match its folder \"{folderName}\".";
        if (string.IsNullOrEmpty(description))
            return "Missing \"description\" in the frontmatter.";
        if (description.Length > 1024)
            return "The description is longer than 1024 characters.";
        return null;
    }

    /// <summary>The model's skill tools, over the skills enabled right now.</summary>
    public static IEnumerable<AITool> CreateTools(Func<IReadOnlyList<Skill>> skills)
    {
        var current = skills();
        if (current.Count == 0)
            yield break;

        var catalog = new StringBuilder("Load a skill: expert instructions for a kind of task. When a task matches one of these, call this first and follow what it says.\nAvailable skills:");
        foreach (var skill in current)
            catalog.Append("\n- ").Append(skill.Name).Append(": ").Append(skill.Description.ReplaceLineEndings(" "));

        yield return AIFunctionFactory.Create(
            ([Description("The skill's name, exactly as listed.")] string name) =>
            {
                var skill = skills().FirstOrDefault(s => s.Name == name)
                    ?? throw new ArgumentException($"No skill named '{name}'. Available: {string.Join(", ", skills().Select(s => s.Name))}");
                var (_, _, body) = Parse(File.ReadAllText(Path.Combine(skill.Directory, "SKILL.md")));
                var files = SupportingFiles(skill).ToList();
                return files.Count == 0
                    ? body
                    : body + "\n\n## Supporting files\nRead these with read_skill_file when the instructions refer to them:\n"
                        + string.Join("\n", files.Select(f => "- " + f));
            },
            "load_skill",
            catalog.ToString());

        yield return AIFunctionFactory.Create(
            ([Description("The skill's name.")] string name,
             [Description("Path of the file relative to the skill folder, e.g. references/REFERENCE.md.")] string path) =>
            {
                var skill = skills().FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"No skill named '{name}'.");
                var root = Path.GetFullPath(skill.Directory) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(skill.Directory, path));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    throw new ArgumentException($"'{path}' is not a file in the skill '{name}'.");
                var info = new FileInfo(full);
                if (info.Length > 256 * 1024)
                    throw new ArgumentException($"'{path}' is too large to read ({info.Length:N0} bytes).");
                return File.ReadAllText(full);
            },
            "read_skill_file",
            "Read a supporting file of a loaded skill (relative path inside the skill folder).");
    }

    private static IEnumerable<string> SupportingFiles(Skill skill) =>
        System.IO.Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skill.Directory, f).Replace('\\', '/'))
            .Where(f => !string.Equals(f, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(100);

    [GeneratedRegex(@"\A﻿?---\r?\n(?<yaml>[\s\S]*?)\r?\n---[ \t]*(\r?\n|\z)")]
    private static partial Regex Frontmatter();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SkillName();
}
