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
    private const int MaxReadChars = 100_000;
    private const long MaxSearchFileBytes = 2 * 1024 * 1024;

    // Heavy folders that are almost never what the user means when listing or searching recursively.
    private static readonly string[] s_skippedFolders = ["node_modules", ".git", "bin", "obj", ".vs"];

    public static IEnumerable<AITool> Create(Sandbox sandbox, IToolApprover approver, ToolPermissionStore permissions)
    {
        AITool Gate(AIFunction function, bool readOnly) =>
            new ApprovalGatedFunction(function, ServerName, function.Name, readOnly, approver, permissions);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Folder relative to the sandbox root; \".\" for the root.")] string path = ".",
             [Description("List everything below the folder, not just its direct children.")] bool recursive = false) =>
                Run(() => ListDirectory(sandbox, path, recursive)),
            "list_directory",
            "List files and folders in the sandbox folder (the only place file tools can access). Start here to see what's available."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root.")] string path,
             [Description("First line to return (1-based).")] int offset = 1,
             [Description("Maximum number of lines to return.")] int limit = 400) =>
                Run(() => ReadFile(sandbox, path, offset, limit)),
            "read_file",
            "Read a text file from the sandbox folder. Also extracts the text of .docx, .xlsx, .pptx and .pdf files."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Glob pattern, e.g. \"**/*.md\" or \"reports/*.xlsx\".")] string pattern,
             [Description("Folder to search in, relative to the sandbox root.")] string path = ".") =>
                Run(() => FindFiles(sandbox, pattern, path)),
            "find_files",
            "Find files in the sandbox folder by name using a glob pattern."), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Text to look for (or a regular expression if regex is true).")] string query,
             [Description("Folder to search in, relative to the sandbox root.")] string path = ".",
             [Description("Glob limiting which files are searched, e.g. \"**/*.cs\".")] string file_pattern = "**/*",
             [Description("Treat query as a .NET regular expression.")] bool regex = false,
             [Description("Match case exactly.")] bool case_sensitive = false) =>
                Run(() => SearchText(sandbox, query, path, file_pattern, regex, case_sensitive)),
            "search_text",
            "Search the contents of text files in the sandbox folder. Returns matching lines as path:line: text. (Office/PDF files aren't searched; use read_file for those.)"), readOnly: true);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root. Parent folders are created as needed.")] string path,
             [Description("Complete new file content.")] string content) =>
                Run(() => WriteFile(sandbox, path, content)),
            "write_file",
            "Create a text file in the sandbox folder, or overwrite it completely. For changes to an existing file prefer edit_file."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("File path relative to the sandbox root.")] string path,
             [Description("Exact text to replace, copied from the file (include enough context to be unique).")] string old_text,
             [Description("Replacement text.")] string new_text,
             [Description("Replace every occurrence instead of requiring old_text to be unique.")] bool replace_all = false) =>
                Run(() => EditFile(sandbox, path, old_text, new_text, replace_all)),
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
                Run(() => Move(sandbox, source, destination)),
            "move_file",
            "Move or rename a file or folder within the sandbox folder."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Output path relative to the sandbox root, ending in .docx. Overwrites an existing file.")] string path,
             [Description("Document content in Markdown: # headings, paragraphs, **bold**, *italic*, - bullet and 1. numbered lists (nested by indenting), | pipe | tables |, > quotes, ``` code blocks, [links](https://…).")] string markdown) =>
                Run(() => CreateOfficeFile(sandbox, path, ".docx", file => OfficeWriter.WriteWordDocument(file, markdown))),
            "create_word_document",
            "Create a real Word document (.docx) in the sandbox folder from Markdown, with proper heading styles, lists and tables. Use this whenever the user asks for a Word/docx file — never write RTF or HTML instead."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("Output path relative to the sandbox root, ending in .xlsx. Overwrites an existing file.")] string path,
             [Description("Sheets in order: sheet name → rows, each row an array of cell values. The first row is the header. Use JSON numbers for numeric values; strings starting with \"=\" are Excel formulas (e.g. \"=SUM(B2:B4)\", \"=AVERAGE('Sheet 1'!B2:B4)\"). Example: {\"Sales\": [[\"Month\", \"Revenue\"], [\"Jan\", 1200]]}.")] Dictionary<string, List<List<JsonElement>>> sheets) =>
                Run(() => sheets is not { Count: > 0 }
                    ? "Error: sheets is empty."
                    : CreateOfficeFile(sandbox, path, ".xlsx", file => OfficeWriter.WriteWorkbook(file, sheets))),
            "create_excel_workbook",
            "Create a real Excel workbook (.xlsx) in the sandbox folder from tabular data: one or more sheets with a bold, frozen, filterable header row and numbers stored as numbers. Use this whenever the user asks for an Excel/xlsx file or a spreadsheet — CSV only if they ask for CSV."), readOnly: false);
    }

    /// <summary>
    /// Writes to a temp file first so a failure never leaves a half-written document behind (or destroys
    /// the one being replaced). The full path in the result lets the chat show an "open" link.
    /// </summary>
    private static string CreateOfficeFile(Sandbox sandbox, string path, string extension, Action<string> write)
    {
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path += extension;

        var file = sandbox.Resolve(path);
        if (Directory.Exists(file))
            return $"Error: '{path}' is a folder.";

        var existed = File.Exists(file);
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

    private static string ListDirectory(Sandbox sandbox, string path, bool recursive)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var output = new StringBuilder();
        if (folder == sandbox.Root)
            output.AppendLine($"Sandbox folder: {sandbox.Root}");

        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MaxRecursionDepth = 12 };
        var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options)
            .Where(e => !IsInSkippedFolder(sandbox, e.FullName))
            .OrderBy(e => e is FileInfo)
            .ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListEntries + 1)
            .ToList();

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
        var output = new StringBuilder();
        var end = start - 1;
        foreach (var line in lines.Skip(start - 1).Take(Math.Max(1, limit)))
        {
            if (output.Length + line.Length > MaxReadChars)
                break;
            output.AppendLine(line);
            end++;
        }

        var header = end < lines.Length || start > 1
            ? $"{sandbox.Relative(file)} — lines {start}-{end} of {lines.Length} (use offset to read more)"
            : $"{sandbox.Relative(file)} — {lines.Length} lines";
        return $"{header}\n\n{output}";
    }

    private static string FindFiles(Sandbox sandbox, string pattern, string path)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var results = Glob(folder, pattern).Where(f => !IsInSkippedFolder(sandbox, f)).Take(MaxFindResults + 1).ToList();
        if (results.Count == 0)
            return $"No files match '{pattern}'.";

        var output = new StringBuilder();
        foreach (var file in results.Take(MaxFindResults))
            output.AppendLine(sandbox.Relative(file));
        if (results.Count > MaxFindResults)
            output.AppendLine($"[… more than {MaxFindResults} files; narrow the pattern]");
        return output.ToString();
    }

    private static string SearchText(Sandbox sandbox, string query, string path, string filePattern, bool regex, bool caseSensitive)
    {
        var folder = sandbox.Resolve(path);
        if (!Directory.Exists(folder))
            return $"Error: folder '{path}' doesn't exist.";

        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var matcher = new Regex(regex ? query : Regex.Escape(query), options, TimeSpan.FromSeconds(1));

        var output = new StringBuilder();
        var matches = 0;
        var filesSearched = 0;
        foreach (var file in Glob(folder, filePattern))
        {
            if (IsInSkippedFolder(sandbox, file) || DocumentText.IsDocument(file) ||
                new FileInfo(file).Length > MaxSearchFileBytes || IsBinary(file))
            {
                continue;
            }

            filesSearched++;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!matcher.IsMatch(line))
                    continue;

                var text = line.Trim();
                output.AppendLine($"{sandbox.Relative(file)}:{lineNumber}: {(text.Length > 200 ? text[..200] + "…" : text)}");
                if (++matches >= MaxSearchMatches)
                {
                    output.AppendLine($"[… stopped at {MaxSearchMatches} matches; narrow the search]");
                    return output.ToString();
                }
            }
        }

        return matches == 0 ? $"No matches for '{query}' in {filesSearched} files." : output.ToString();
    }

    private static string WriteFile(Sandbox sandbox, string path, string content)
    {
        var file = sandbox.Resolve(path);
        if (Directory.Exists(file))
            return $"Error: '{path}' is a folder.";

        var existed = File.Exists(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return $"{(existed ? "Overwrote" : "Created")} {sandbox.Relative(file)} ({FormatSize(new FileInfo(file).Length)}).";
    }

    private static string EditFile(Sandbox sandbox, string path, string oldText, string newText, bool replaceAll)
    {
        var file = sandbox.Resolve(path);
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
        File.WriteAllText(file, updated, encoding);
        return $"Edited {sandbox.Relative(file)}: replaced {(replaceAll ? count : 1)} occurrence(s).";
    }

    private static string CreateDirectory(Sandbox sandbox, string path)
    {
        var folder = sandbox.Resolve(path);
        if (File.Exists(folder))
            return $"Error: '{path}' is an existing file.";
        Directory.CreateDirectory(folder);
        return $"Created folder {sandbox.Relative(folder)}.";
    }

    private static string Move(Sandbox sandbox, string source, string destination)
    {
        var from = sandbox.Resolve(source);
        var to = sandbox.Resolve(destination);
        if (from == sandbox.Root)
            return "Error: the sandbox root itself can't be moved.";
        if (File.Exists(to) || Directory.Exists(to))
            return $"Error: '{destination}' already exists.";

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else if (File.Exists(from))
            File.Move(from, to);
        else
            return $"Error: '{source}' doesn't exist.";

        return $"Moved {sandbox.Relative(from)} → {sandbox.Relative(to)}.";
    }

    private static IEnumerable<string> Glob(string folder, string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(string.IsNullOrWhiteSpace(pattern) ? "**/*" : pattern.Replace('\\', '/'));
        return matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(folder)))
            .Files.Select(f => Path.GetFullPath(Path.Combine(folder, f.Path)));
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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
