// Harness.WinUI — Licensed under the MIT License.

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Harness.Core.Files;

namespace Harness.Core.Tools;

/// <summary>
/// <c>run_command</c>: runs a PowerShell command in the sandbox folder, after the user approves it. It runs
/// as the user, like a terminal they'd open themselves: the approval is the control, not isolation.
/// "Always allow" covers commands that start the same way (e.g. "uv run"), and only simple ones: a command
/// that chains, pipes, redirects or computes anything is asked every time (see <see cref="AlwaysAllowPrefix"/>).
/// Input is closed (nothing can wait for typing), and the whole process tree is stopped on timeout or when
/// the reply is stopped.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class CommandTool
{
    public const string Name = "run_command";
    public const string ServerName = "shell";

    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 600;
    private const int HeadChars = 8_000;
    private const int TailChars = 22_000;

    private const string Description = """
        Run a PowerShell command on the user's PC, in the sandbox folder, and get its output and exit code.
        The user approves each command before it runs. Use it to run programs and scripts (e.g.
        `uv run python calculator.py`, `node app.js`), build or test code, or check what's installed.
        Input is closed: a program that waits for typed input gets end-of-file, so pass inputs as arguments,
        through a pipe, or in a file. Don't start servers or anything that keeps running.
        """;

    private static readonly Lazy<string> s_shell = new(FindPowerShell);

    public static AIFunction Create(Sandbox sandbox, IToolApprover approver, ToolPermissionStore permissions) =>
        new ApprovalGatedFunction(
            AIFunctionFactory.Create(
                (
                    [Description("The PowerShell command (several lines are fine).")] string command,
                    CancellationToken cancellationToken,
                    [Description("Seconds before the command is stopped (default 120, at most 600).")] int? timeout_seconds = null) =>
                    RunAsync(sandbox.Root, command, TimeSpan.FromSeconds(Math.Clamp(timeout_seconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds)), cancellationToken),
                Name,
                Description),
            ServerName, Name, readOnly: false, approver, permissions,
            scope: arguments => arguments.TryGetValue("command", out var command) ? AlwaysAllowPrefix(command?.ToString()) : null);

    // Anything that could hide another command, or one that runs a shell, deletes, or starts other programs
    // detached, can't be always-allowed: "always allow 'git status'" mustn't also allow "git status; rm …".
    private static readonly SearchValues<char> s_compound = SearchValues.Create(";|&<>`$(){}\r\n");
    private static readonly HashSet<string> s_neverAlways = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "cmd", "bash", "sh", "wsl", "iex", "invoke-expression", "icm", "invoke-command",
        "start", "saps", "start-process", "start-job", "rm", "ri", "del", "erase", "rd", "rmdir", "remove-item",
        "format", "format-volume", "clear-disk", "shutdown", "stop-computer", "restart-computer", "reg", "runas",
        "sudo", "set-executionpolicy", "takeown", "icacls", "schtasks", "sc",
    };

    /// <summary>
    /// What "Always allow" would cover for this command: its program and, if it has one, its subcommand
    /// ("uv run", "git status", "python"); null when the command must be asked every time.
    /// </summary>
    public static string? AlwaysAllowPrefix(string? command)
    {
        var trimmed = command?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.AsSpan().ContainsAny(s_compound))
            return null;

        var words = trimmed.Split(' ', '\t').Where(w => w.Length > 0).ToArray();
        var program = words[0].ToLowerInvariant();
        if (!ProgramWord().IsMatch(program))
            return null;
        if (program.EndsWith(".exe", StringComparison.Ordinal))
            program = program[..^4];
        if (s_neverAlways.Contains(Path.GetFileName(program)))
            return null;

        return words.Length > 1 && SubcommandWord().IsMatch(words[1]) ? $"{program} {words[1].ToLowerInvariant()}" : program;
    }

    [GeneratedRegex(@"^[a-z0-9._\\/:-]+$")]
    private static partial Regex ProgramWord();

    // "run", "status", "build" — not options, paths or file names.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]*$")]
    private static partial Regex SubcommandWord();

    private static async Task<string> RunAsync(string folder, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "No command given.";

        // UTF-8 both ways, no progress bars; then the exit code of the last program run (or 1 if the last
        // PowerShell command failed), as a terminal would show it.
        var script = $$"""
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            {{command}}
            $harnessOk = $?
            if ($LASTEXITCODE) { exit $LASTEXITCODE }
            if (-not $harnessOk) { exit 1 }
            """;

        var start = new ProcessStartInfo(s_shell.Value)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        // Python writes in the console's code page when piped unless told otherwise.
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["NO_COLOR"] = "1";

        var output = new OutputBuffer();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => output.Add(e.Data);
        process.ErrorDataReceived += (_, e) => output.Add(e.Data);

        var timer = Stopwatch.StartNew();
        process.Start();
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It has already exited.
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            cancellationToken.ThrowIfCancellationRequested();
            return $"Stopped after {timeout.TotalSeconds:0} seconds (timed out). Output so far:\n{output}";
        }

        return $"Exit code: {process.ExitCode} ({timer.Elapsed.TotalSeconds:0.0} s)\n{output}";
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>PowerShell 7 when it's installed, otherwise the Windows PowerShell every PC has.</summary>
    private static string FindPowerShell()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "pwsh.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    }

    /// <summary>stdout and stderr lines in arrival order; keeps the start and the end of long output.</summary>
    private sealed class OutputBuffer
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _head = new();
        private readonly Queue<string> _tail = new();
        private int _tailChars;
        private long _dropped;

        public void Add(string? line)
        {
            if (line is null)
                return;
            if (line.Length > TailChars)
                line = line[..TailChars] + "…";
            lock (_lock)
            {
                if (_head.Length + line.Length + 1 <= HeadChars)
                {
                    _head.Append(line).Append('\n');
                    return;
                }
                _tail.Enqueue(line);
                _tailChars += line.Length + 1;
                while (_tailChars > TailChars && _tail.Count > 1)
                {
                    var dropped = _tail.Dequeue();
                    _tailChars -= dropped.Length + 1;
                    _dropped += dropped.Length + 1;
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_head.Length == 0 && _tail.Count == 0)
                    return "(no output)";
                var text = new StringBuilder(_head.ToString());
                if (_dropped > 0)
                    text.Append($"…({_dropped:N0} characters of output left out)…\n");
                foreach (var line in _tail)
                    text.Append(line).Append('\n');
                return text.ToString().TrimEnd();
            }
        }
    }
}
