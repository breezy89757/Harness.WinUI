// Harness.WinUI — Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Harness.Core.Files;

/// <summary>
/// The .gitignore rules of a project (the one at its root and those in folders below, plus .git/info/exclude), so listing
/// and searching leave out what git ignores: build output, packages, local settings. A simplified reading of the format:
/// comments, "!" to bring a path back, a trailing "/" for folders only, "*", "?", "**", and a pattern with a "/" in it
/// being relative to the file it is written in (otherwise it matches at any depth).
/// </summary>
public sealed class GitIgnore
{
    private readonly string _root;
    private readonly Dictionary<string, IReadOnlyList<Rule>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitIgnore(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
    }

    /// <summary>Whether git ignores this path (relative to the project root, "/" separated). A path inside an ignored folder is ignored.</summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var parts = relativePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = string.Empty; // the folder (relative to the root) whose .gitignore is being read
        var prefix = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
            var isLast = i == parts.Length - 1;
            if (Matches(prefix, isLast ? isDirectory : true))
                return true; // an ignored folder takes everything below it with it
            if (!isLast)
                folder = prefix;
        }
        _ = folder;
        return false;
    }

    private bool Matches(string path, bool isDirectory)
    {
        // The rules of every folder on the way down, root first; the last rule that matches decides.
        var ignored = false;
        var parts = path.Split('/');
        var ownerFolder = string.Empty;
        for (var depth = 0; depth < parts.Length; depth++)
        {
            foreach (var rule in RulesOf(ownerFolder))
            {
                var relative = ownerFolder.Length == 0 ? path : path[(ownerFolder.Length + 1)..];
                if (rule.DirectoryOnly && !isDirectory)
                    continue;
                if (rule.Pattern.IsMatch(relative))
                    ignored = !rule.Negated;
            }
            if (depth < parts.Length - 1)
                ownerFolder = ownerFolder.Length == 0 ? parts[depth] : ownerFolder + "/" + parts[depth];
        }
        return ignored;
    }

    private IReadOnlyList<Rule> RulesOf(string folder)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(folder, out var cached))
                return cached;

            var rules = new List<Rule>();
            var directory = folder.Length == 0 ? _root : Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar));
            TryRead(Path.Combine(directory, ".gitignore"), rules);
            if (folder.Length == 0)
                TryRead(Path.Combine(directory, ".git", "info", "exclude"), rules);
            _cache[folder] = rules;
            return rules;
        }
    }

    private static void TryRead(string file, List<Rule> rules)
    {
        try
        {
            if (!File.Exists(file))
                return;
            foreach (var raw in File.ReadLines(file))
            {
                if (Parse(raw) is { } rule)
                    rules.Add(rule);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable .gitignore just means nothing extra is hidden.
        }
    }

    private static Rule? Parse(string line)
    {
        line = line.TrimEnd();
        if (line.Length == 0 || line[0] == '#')
            return null;

        var negated = line[0] == '!';
        if (negated)
            line = line[1..];
        if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
            line = line[1..];

        var directoryOnly = line.EndsWith('/');
        line = line.TrimEnd('/');
        if (line.Length == 0)
            return null;

        // A "/" at the start or in the middle ties the pattern to the folder of the .gitignore; otherwise it matches at any depth.
        var anchored = line.Contains('/');
        line = line.TrimStart('/');
        var body = GlobToRegex(line);
        var regex = anchored ? $"^{body}$" : $"(?:^|/){body}$";
        try
        {
            return new Rule(new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), negated, directoryOnly);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string GlobToRegex(string glob)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // "**/" is any number of folders (or none); a trailing "/**" is everything below; a lone "**" is anything.
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        text.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        text.Append(".*");
                        i++;
                    }
                    break;
                case '*':
                    text.Append("[^/]*");
                    break;
                case '?':
                    text.Append("[^/]");
                    break;
                case '[':
                    var end = glob.IndexOf(']', i + 1);
                    if (end > i + 1)
                    {
                        text.Append(glob[i..(end + 1)].Replace("[!", "[^"));
                        i = end;
                    }
                    else
                    {
                        text.Append("\\[");
                    }
                    break;
                case '\\' when i + 1 < glob.Length:
                    text.Append(Regex.Escape(glob[++i].ToString()));
                    break;
                default:
                    text.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return text.ToString();
    }

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);
}
