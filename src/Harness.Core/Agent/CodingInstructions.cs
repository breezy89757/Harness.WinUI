// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Agent;

/// <summary>
/// What the model is told when the sandbox folder is a code project: read before answering, search instead of guessing,
/// change in small steps and check. Only added for such a folder, so everyone else doesn't pay for the tokens.
/// </summary>
public static class CodingInstructions
{
    // A folder with one of these at its root is treated as a project.
    private static readonly string[] s_projectFolders = [".git"];

    private static readonly string[] s_projectFiles =
    [
        "*.sln", "*.slnx", "*.csproj", "package.json", "pyproject.toml", "requirements.txt", "go.mod", "Cargo.toml",
        "pom.xml", "build.gradle", "CMakeLists.txt", "Makefile", "AGENTS.md", "CLAUDE.md",
    ];

    private static readonly Lock s_lock = new();
    private static string? s_lastRoot;
    private static bool s_lastResult;

    /// <summary>Whether <paramref name="root"/> looks like a code project (remembered for the folder last asked about).</summary>
    public static bool LooksLikeProject(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return false;

        lock (s_lock)
        {
            if (string.Equals(s_lastRoot, root, StringComparison.OrdinalIgnoreCase))
                return s_lastResult;
        }

        bool result;
        try
        {
            result = s_projectFolders.Any(f => Directory.Exists(Path.Combine(root, f))) ||
                s_projectFiles.Any(p => Directory.EnumerateFiles(root, p, SearchOption.TopDirectoryOnly).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = false;
        }

        lock (s_lock)
        {
            s_lastRoot = root;
            s_lastResult = result;
        }
        return result;
    }

    /// <summary>Forgets what was learned about the folder (it may have gained a .git or a project file).</summary>
    public static void Forget()
    {
        lock (s_lock)
            s_lastRoot = null;
    }

    public const string Text = """
        Working on code in the sandbox folder (it looks like a project):
        - Read before you answer or change anything. Read the files that matter in full: read_file numbers the lines and says how many remain, so keep going with offset until you have the whole picture. A file name tells you little; don't describe, summarize or draw code you have only seen listed. If you haven't read something, say so or read it.
        - Start with AGENTS.md / README and the entry points, then follow the code: find where things are defined and used with search_text (regex, context_lines, files_only) instead of guessing paths.
        - Change code in small steps with edit_file (old_text copied exactly, without the line numbers), then check the result: build or run the tests with run_command and fix what fails. Re-read the changed region when unsure.
        - Don't paste whole files back; say in a few lines what you found or changed and where (path:line).
        """;
}
