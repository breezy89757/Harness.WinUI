// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Harness.Core.Tools;

namespace Harness.Core.Files;

/// <summary>
/// In-process file tools scoped to a <see cref="Sandbox"/> — replaces the Node filesystem MCP server
/// (no extra processes, no startup wait) and adds text extraction for Office/PDF files.
/// </summary>
public static class FileTools
{
    public const string ServerName = "files";

    private const int MaxListEntries = 300;
    private const int MaxFindResults = 200;
    private const int MaxSearchMatches = 100;
    private const int MaxReadChars = 100_000;     // text extracted from Office / PDF files
    private const int MaxReadTextChars = 300_000;  // one page of a text file (about 75k tokens)
    private const int DefaultReadLines = 2000;
    private const int MaxContextLines = 5;
    private const long MaxSearchFileBytes = 2 * 1024 * 1024;

    // Heavy folders that are almost never what the user means when listing or searching recursively.
    private static readonly string[] s_skippedFolders = ["node_modules", ".git", "bin", "obj", ".vs"];

    /// <param name="includeOffice">Offer create_word_document and create_excel_workbook (Code mode leaves them out).</param>
    /// <param name="checkpoints">Where the earlier state of a file is saved before it is changed, so a turn's changes can be undone.</param>
    public static IEnumerable<AITool> Create(
        Sandbox sandbox, IToolApprover approver, ToolPermissionStore permissions, bool includeOffice = true, FileCheckpoints? checkpoints = null)
    {
        AITool Gate(AIFunction function, bool readOnly) =>
            new ApprovalGatedFunction(function, ServerName, function.Name, readOnly, approver, permissions);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Folder relative to the sandbox root; \".\" for the root.")] string path = ".",
             [Description("List everything below the folder, not just its direct children.")] bool recursive = false,
             [Description("Also list what the project's .gitignore ignores (build output, packages).")] bool include_ignored = false) =>
                Run(() => ListDirectory(sandbox, path, recursive, include_ignored)),
            "list_directory",
            "List files and folders in the sandbox folder (the only place file tools can access). Start here to see what's available. Files that .gitignore ignores are left out unless include_ignored is set."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root.")] string path,
             [Description("First line to return (1-based).")] int offset = 1,
             [Description("Maximum number of lines to return (default 2000: most files are read whole).")] int limit = DefaultReadLines) =>
                Run(() => ReadFile(sandbox, path, offset, limit)),
            "read_file",
            "Read a text file from the sandbox folder. Each line starts with its line number and an arrow; those are not part of the file, so leave them out of edit_file's old_text. " +
            "The result says how many lines the file has and how many remain; continue with offset until you have read what you need. " +
            "Also extracts the text of .docx, .xlsx, .pptx and .pdf files."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Glob pattern, e.g. \"**/*.md\" or \"reports/*.xlsx\".")] string pattern,
             [Description("Folder to search in, relative to the sandbox root.")] string path = ".",
             [Description("Also find files that the project's .gitignore ignores.")] bool include_ignored = false) =>
                Run(() => FindFiles(sandbox, pattern, path, include_ignored)),
            "find_files",
            "Find files in the sandbox folder by name using a glob pattern. Files that .gitignore ignores are left out unless include_ignored is set."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Text to look for (or a regular expression if regex is true).")] string query,
             [Description("Folder to search in, relative to the sandbox root.")] string path = ".",
             [Description("Glob limiting which files are searched, e.g. \"**/*.cs\".")] string file_pattern = "**/*",
             [Description("Treat query as a .NET regular expression.")] bool regex = false,
             [Description("Match case exactly.")] bool case_sensitive = false,
             [Description("Lines of context to show before and after each match (0 to 5).")] int context_lines = 0,
             [Description("Only list the files that contain a match, with the number of matches in each.")] bool files_only = false,
             [Description("Also search files that the project's .gitignore ignores.")] bool include_ignored = false) =>
                Run(() => SearchText(sandbox, query, path, file_pattern, regex, case_sensitive, context_lines, files_only, include_ignored)),
            "search_text",
            "Search the contents of text files in the sandbox folder. Returns matching lines as path:line: text (context lines as path-line- text). Use it to find where something is defined or used instead of guessing paths. (Office/PDF files aren't searched; use read_file for those.)"), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root. Parent folders are created as needed.")] string path,
             [Description("Complete new file content.")] string content) =>
                Run(() => WriteFile(sandbox, path, content, checkpoints)),
            "write_file",
            "Create a text file in the sandbox folder, or overwrite it completely. For changes to an existing file prefer edit_file."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root.")] string path,
             [Description("Exact text to replace, copied from the file (include enough context to be unique).")] string old_text,
             [Description("Replacement text.")] string new_text,
             [Description("Replace every occurrence instead of requiring old_text to be unique.")] bool replace_all = false) =>
                Run(() => EditFile(sandbox, path, old_text, new_text, replace_all, checkpoints)),
            "edit_file",
            "Replace exact text in a file in the sandbox folder. Read the file first so old_text matches exactly."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Folder path relative to the sandbox root.")] string path) =>
                Run(() => CreateDirectory(sandbox, path)),
            "create_directory",
            "Create a folder (and any missing parents) in the sandbox folder."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Existing file or folder, relative to the sandbox root.")] string source,
             [Description("New path, relative to the sandbox root. Must not exist yet.")] string destination) =>
                Run(() => Move(sandbox, source, destination, checkpoints)),
            "move_file",
            "Move or rename a file or folder within the sandbox folder."), readOnly: false);

        if (!includeOffice)
            yield break;

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Output path relative to the sandbox root, ending in .docx. Overwrites an existing file.")] string path,
             [Description("Document content in Markdown: # headings, paragraphs, **bold**, *italic*, - bullet and 1. numbered lists (nested by indenting), | pipe | tables |, > quotes, ``` code blocks, [links](https://…).")] string markdown) =>
                Run(() => CreateOfficeFile(sandbox, path, ".docx", file => OfficeWriter.WriteWordDocument(file, markdown), checkpoints)),
            "create_word_document",
            "Create a real Word document (.docx) in the sandbox folder from Markdown, with proper heading styles, lists and tables. Use this whenever the user asks for a Word/docx file — never write RTF or HTML instead."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Output path relative to the sandbox root, ending in .xlsx. Overwrites an existing file.")] string path,
             [Description("Sheets in order: sheet name → rows, each row an array of cell values. The first row is the header. Use JSON numbers for numeric values; strings starting with \"=\" are Excel formulas (e.g. \"=SUM(B2:B4)\", \"=AVERAGE('Sheet 1'!B2:B4)\"). Example: {\"Sales\": [[\"Month\", \"Revenue\"], [\"Jan\", 1200]]}.")] Dictionary<string, List<List<JsonElement>>> sheets) =>
                Run(() => sheets is not { Count: > 0 }
                    ? "Error: sheets is empty."
                    : CreateOfficeFile(sandbox, path, ".xlsx", file => OfficeWriter.WriteWorkbook(file, sheets), checkpoints)),
            "create_excel_workbook",
            "Create a real Excel workbook (.xlsx) in the sandbox folder from tabular data: one or more sheets with a bold, frozen, filterable header row and numbers stored as numbers. Use this whenever the user asks for an Excel/xlsx file or a spreadsheet — CSV only if they ask for CSV."), readOnly: false);
    }

    /// <summary>
    /// Writes to a temp file first so a failure never leaves a half-written document behind (or destroys
    /// the one being replaced). The full path in the result lets the chat show an "open" link.
    /// </summary>
    private static string CreateOfficeFile(Sandbox sandbox, string path, string extension, Action<string> write, FileCheckpoints? checkpoints)
    {
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path += extension;

        var file = sandbox.Resolve(path);
        if (Directory.Exists(file))
            return $"Error: '{path}' is a folder.";

        var existed = File.Exists(file);
        checkpoints?.BeforeChange(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        try
        {
            write(temp);
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException ex) when (existed && File.Exists(temp))
        {
            return $"Error: couldn't replace {sandbox.Relative(file)} — is it open in another program? ({ex.Message})";
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or UnauthorizedAccessException))
        {
            return $"Error: couldn't create {sandbox.Relative(file)}: {ex.Message}";
        }
        finally
        {
            File.Delete(temp);
        }

        return $"{(existed ? "Overwrote" : "Created")} {sandbox.Relative(file)} ({FormatSize(new FileInfo(file).Length)}).\n{CreatedFilePrefix}{file}";
    }

    /// <summary>Marks the line in a create_* result that holds the full path of the file written.</summary>
    public const string CreatedFilePrefix = "Full path: ";

    /// <summary>Expected failures go back to the model as text so it can correct itself.</summary>
    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException or InvalidDataException)
        {
            return "Error: " + ex.Message;
        }
    }

    private static string ListDirectory(Sandbox sandbox, string path, bool recursive, bool includeIgnored)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var output = new StringBuilder();
        if (folder == sandbox.Root)
            output.AppendLine($"Sandbox folder: {sandbox.Root}");

        var ignore = includeIgnored ? null : new GitIgnore(sandbox.Root);
        var entries = Entries(sandbox, folder, recursive, ignore).Take(MaxListEntries + 1).ToList();

        if (entries.Count == 0)
            output.AppendLine("(empty)");
        foreach (var entry in entries.Take(MaxListEntries))
        {
            var name = recursive ? sandbox.Relative(entry.FullName) : entry.Name;
            output.AppendLine(entry is FileInfo file ? $"{name}  ({FormatSize(file.Length)})" : name + "/");
        }
        if (entries.Count > MaxListEntries)
            output.AppendLine($"[… more than {MaxListEntries} entries; list a subfolder or use find_files]");

        return output.ToString();
    }

    /// <summary>The entries of a folder (folders first, then by name), going down into folders that aren't skipped or ignored.</summary>
    private static IEnumerable<FileSystemInfo> Entries(Sandbox sandbox, string folder, bool recursive, GitIgnore? ignore)
    {
        var found = new List<FileSystemInfo>();
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((folder, 0));
        while (pending.Count > 0 && found.Count <= MaxListEntries * 4)
        {
            var (current, depth) = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var isDirectory = child is DirectoryInfo;
                if (IsSkipped(sandbox, child.FullName, isDirectory, ignore))
                    continue;
                found.Add(child);
                if (recursive && isDirectory && depth < 12)
                    pending.Push((child.FullName, depth + 1));
            }
        }

        return found.OrderBy(e => e is FileInfo).ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A folder or file that listing and searching leave out: heavy folders, and what .gitignore ignores.</summary>
    private static bool IsSkipped(Sandbox sandbox, string fullPath, bool isDirectory, GitIgnore? ignore)
    {
        if (isDirectory ? s_skippedFolders.Contains(Path.GetFileName(fullPath), StringComparer.OrdinalIgnoreCase) : IsInSkippedFolder(sandbox, fullPath))
            return true;
        return ignore?.IsIgnored(sandbox.Relative(fullPath), isDirectory) == true;
    }

    private static string ReadFile(Sandbox sandbox, string path, int offset, int limit)
    {
        var file = sandbox.Resolve(path);
        if (!File.Exists(file))
            return $"Error: file '{path}' doesn't exist.";

        if (DocumentText.IsDocument(file))
        {
            try
            {
                return $"{sandbox.Relative(file)} (text extracted from {Path.GetExtension(file)})\n\n{DocumentText.Extract(file, MaxReadChars)}";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Corrupt, encrypted or password-protected documents throw library-specific exceptions.
                return $"Error: couldn't extract text from '{path}': {ex.Message}";
            }
        }

        if (IsBinary(file))
            return $"Error: '{path}' is a binary file ({FormatSize(new FileInfo(file).Length)}) and can't be read as text.";

        var lines = File.ReadAllLines(file);
        var start = Math.Clamp(offset, 1, Math.Max(1, lines.Length));
        var width = Math.Max(4, lines.Length.ToString().Length);
        var output = new StringBuilder();
        var end = start - 1;
        foreach (var line in lines.Skip(start - 1).Take(Math.Max(1, limit)))
        {
            if (output.Length + line.Length > MaxReadTextChars)
                break;
            output.Append((end + 1).ToString().PadLeft(width)).Append('\u2192').AppendLine(line);
            end++;
        }

        var header = end < lines.Length || start > 1
            ? $"{sandbox.Relative(file)} — lines {start}-{end} of {lines.Length}" +
              (end < lines.Length ? $"; {lines.Length - end} more: call read_file again with offset={end + 1}" : "")
            : $"{sandbox.Relative(file)} — {lines.Length} lines (the whole file)";
        return $"{header}\n\n{output}";
    }

    private static string FindFiles(Sandbox sandbox, string pattern, string path, bool includeIgnored)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var results = Walk(sandbox, folder, pattern, includeIgnored ? null : new GitIgnore(sandbox.Root)).Take(MaxFindResults + 1).ToList();
        if (results.Count == 0)
            return $"No files match '{pattern}'.";

        var output = new StringBuilder();
        foreach (var file in results.Take(MaxFindResults))
            output.AppendLine(sandbox.Relative(file));
        if (results.Count > MaxFindResults)
            output.AppendLine($"[… more than {MaxFindResults} files; narrow the pattern]");
        return output.ToString();
    }

    private static string SearchText(
        Sandbox sandbox, string query, string path, string filePattern, bool regex, bool caseSensitive, int contextLines, bool filesOnly, bool includeIgnored)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var context = Math.Clamp(contextLines, 0, MaxContextLines);
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var matcher = new Regex(regex ? query : Regex.Escape(query), options, TimeSpan.FromSeconds(1));

        var output = new StringBuilder();
        var matches = 0;
        var filesSearched = 0;
        foreach (var file in Walk(sandbox, folder, filePattern, includeIgnored ? null : new GitIgnore(sandbox.Root)))
        {
            if (DocumentText.IsDocument(file) ||
                new FileInfo(file).Length > MaxSearchFileBytes || IsBinary(file))
            {
                continue;
            }

            filesSearched++;
            var lines = File.ReadAllLines(file);
            var relative = sandbox.Relative(file);
            var hits = Enumerable.Range(0, lines.Length).Where(i => matcher.IsMatch(lines[i])).ToList();
            if (hits.Count == 0)
                continue;

            if (filesOnly)
            {
                output.AppendLine($"{relative}  ({hits.Count} match{(hits.Count == 1 ? "" : "es")})");
                matches += hits.Count;
                if (matches >= MaxSearchMatches)
                {
                    output.AppendLine($"[… stopped after {matches} matches; narrow the search]");
                    return output.ToString();
                }
                continue;
            }

            var shown = -1; // last line index already written for this file
            foreach (var hit in hits)
            {
                var from = Math.Max(hit - context, shown + 1);
                if (context > 0 && shown >= 0 && from > shown + 1)
                    output.AppendLine("--");
                for (var i = from; i <= Math.Min(lines.Length - 1, hit + context); i++)
                {
                    if (i <= shown)
                        continue;
                    var text = lines[i].Trim();
                    var mark = i == hit ? ':' : '-';
                    output.AppendLine($"{relative}{mark}{i + 1}{mark} {(text.Length > 200 ? text[..200] + "…" : text)}");
                    shown = i;
                }

                if (++matches >= MaxSearchMatches)
                {
                    output.AppendLine($"[… stopped at {MaxSearchMatches} matches; narrow the search]");
                    return output.ToString();
                }
            }
        }

        return matches == 0 ? $"No matches for '{query}' in {filesSearched} files." : output.ToString();
    }

    private static string WriteFile(Sandbox sandbox, string path, string content, FileCheckpoints? checkpoints)
    {
        var file = sandbox.ResolveForWrite(path);
        if (Directory.Exists(file))
            return $"Error: '{path}' is a folder.";

        var existed = File.Exists(file);
        var before = existed && !DocumentText.IsDocument(file) && !IsBinary(file) ? File.ReadAllText(file) : null;
        checkpoints?.BeforeChange(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var summary = $"{(existed ? "Overwrote" : "Created")} {sandbox.Relative(file)} ({FormatSize(new FileInfo(file).Length)}).";
        return before is null ? summary : $"{summary}\n\n{ChangeSummary(before, content)}";
    }

    private static string EditFile(Sandbox sandbox, string path, string oldText, string newText, bool replaceAll, FileCheckpoints? checkpoints)
    {
        var file = sandbox.ResolveForWrite(path);
        if (!File.Exists(file))
            return $"Error: file '{path}' doesn't exist.";
        if (DocumentText.IsDocument(file) || IsBinary(file))
            return $"Error: '{path}' isn't a plain text file and can't be edited with edit_file.";
        if (string.IsNullOrEmpty(oldText))
            return "Error: old_text is empty.";

        Encoding encoding;
        string content;
        using (var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true))
        {
            content = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        // Models usually send "\n"; match files that use "\r\n" too.
        if (!content.Contains(oldText, StringComparison.Ordinal) && content.Contains("\r\n", StringComparison.Ordinal))
        {
            oldText = oldText.ReplaceLineEndings("\r\n");
            newText = newText.ReplaceLineEndings("\r\n");
        }

        var count = CountOccurrences(content, oldText);
        if (count == 0)
            return $"Error: old_text wasn't found in {sandbox.Relative(file)}. Read the file again and copy the text exactly.";
        if (count > 1 && !replaceAll)
            return $"Error: old_text appears {count} times in {sandbox.Relative(file)}. Include more surrounding text to make it unique, or set replace_all.";

        var updated = replaceAll ? content.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(content, oldText, newText);
        checkpoints?.BeforeChange(file);
        File.WriteAllText(file, updated, encoding);
        return $"Edited {sandbox.Relative(file)}: replaced {(replaceAll ? count : 1)} occurrence(s).\n\n{ChangeSummary(content, updated)}";
    }

    private static string CreateDirectory(Sandbox sandbox, string path)
    {
        var folder = sandbox.Resolve(path);
        if (File.Exists(folder))
            return $"Error: '{path}' is an existing file.";
        Directory.CreateDirectory(folder);
        return $"Created folder {sandbox.Relative(folder)}.";
    }

    private static string Move(Sandbox sandbox, string source, string destination, FileCheckpoints? checkpoints)
    {
        var from = sandbox.ResolveForWrite(source);
        var to = sandbox.ResolveForWrite(destination);
        if (from == sandbox.Root)
            return "Error: the sandbox root itself can't be moved.";
        if (File.Exists(to) || Directory.Exists(to))
            return $"Error: '{destination}' already exists.";

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from) || File.Exists(from))
            checkpoints?.BeforeMove(from, to);
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else if (File.Exists(from))
            File.Move(from, to);
        else
            return $"Error: '{source}' doesn't exist.";

        return $"Moved {sandbox.Relative(from)} → {sandbox.Relative(to)}.";
    }

    /// <summary>
    /// The files below <paramref name="folder"/> that match the glob <paramref name="pattern"/> (relative to that folder), leaving
    /// out skipped and ignored folders without going into them (a big node_modules costs nothing).
    /// </summary>
    private static IEnumerable<string> Walk(Sandbox sandbox, string folder, string pattern, GitIgnore? ignore)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(string.IsNullOrWhiteSpace(pattern) ? "**/*" : pattern.Replace('\\', '/'));

        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            List<string> directories;
            List<string> files;
            try
            {
                directories = [.. Directory.EnumerateDirectories(current).Order(StringComparer.OrdinalIgnoreCase)];
                files = [.. Directory.EnumerateFiles(current).Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsSkipped(sandbox, file, isDirectory: false, ignore))
                    continue;
                if (matcher.Match(Path.GetRelativePath(folder, file).Replace('\\', '/')).HasMatches)
                    yield return file;
            }
            // Pushed in reverse so the folders come off the stack in name order.
            foreach (var directory in Enumerable.Reverse(directories))
            {
                if (!IsSkipped(sandbox, directory, isDirectory: true, ignore))
                    pending.Push(directory);
            }
        }
    }

    private static bool IsInSkippedFolder(Sandbox sandbox, string fullPath) =>
        sandbox.Relative(fullPath).Split('/').SkipLast(1).Any(part => s_skippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase));

    /// <summary>A NUL byte in the first 8 KB is a reliable sign of a binary file.</summary>
    private static bool IsBinary(string file)
    {
        Span<byte> buffer = stackalloc byte[8192];
        using var stream = File.OpenRead(file);
        var read = stream.Read(buffer);
        return buffer[..read].Contains((byte)0);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    /// <summary>"+3 −1" and the changed lines with a little context, so the model (and the person reading the step) sees what changed.</summary>
    private static string ChangeSummary(string before, string after)
    {
        var (added, removed) = TextDiff.Count(before, after);
        var diff = TextDiff.Unified(before, after, context: 2, maxOutputLines: 40);
        return diff.Length == 0 ? "(no change)" : $"+{added} -{removed}\n{diff}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
