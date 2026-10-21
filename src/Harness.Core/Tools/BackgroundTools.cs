// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.AI;
using Harness.Core.Files;

namespace Harness.Core.Tools;

/// <summary>
/// Programs the agent started and left running (a dev server, a test watcher): their output is kept so it can be read later,
/// and they are stopped when asked, and when the app closes. At most <see cref="MaxRunning"/> at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackgroundProcesses : IDisposable
{
    public const int MaxRunning = 4;
    private const int MaxKeptLines = 3000;
    private const int MaxLineChars = 2000;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private int _nextId = 1;

    /// <summary>Raised when one starts, exits or is stopped.</summary>
    public event EventHandler? Changed;

    public int RunningCount
    {
        get
        {
            lock (_lock)
                return _entries.Values.Count(e => !e.Exited);
        }
    }

    public string Start(string folder, string command, string? name)
    {
        Entry entry;
        lock (_lock)
        {
            if (_entries.Values.Count(e => !e.Exited) >= MaxRunning)
                return $"Error: {MaxRunning} background programs are already running. Stop one with stop_process first.";

            var process = new Process { StartInfo = CommandTool.BuildStartInfo(folder, command), EnableRaisingEvents = true };
            entry = new Entry(_nextId++, command, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), process);
            process.OutputDataReceived += (_, e) => entry.Add(e.Data);
            process.ErrorDataReceived += (_, e) => entry.Add(e.Data);
            process.Exited += (_, _) =>
            {
                entry.MarkExited();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            try
            {
                process.Start();
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                return $"Error: couldn't start it: {ex.Message}";
            }
            _entries[entry.Id] = entry;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return $"Started background program {entry.Id}{(entry.Name is null ? "" : $" ({entry.Name})")}. Read its output with process_output (id {entry.Id}); stop it with stop_process.";
    }

    /// <summary>New output since the last read (or everything kept, for a first read), with the program's state.</summary>
    public async Task<string> ReadAsync(int? id, int waitSeconds, CancellationToken cancellationToken)
    {
        if (id is null)
        {
            lock (_lock)
            {
                return _entries.Count == 0
                    ? "No background programs."
                    : string.Join("\n", _entries.Values.Select(e => $"{e.Id}: {e.Summary()}"));
            }
        }

        Entry? entry;
        lock (_lock)
            _entries.TryGetValue(id.Value, out entry);
        if (entry is null)
            return $"Error: no background program with id {id}.";

        // A short wait lets a server finish starting, or a build print its result, before the output is read.
        // It stops waiting early when the program has exited, or has printed something and then gone quiet for a moment.
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 60));
        while (DateTime.UtcNow < until && !entry.Exited && !(entry.HasUnread && entry.QuietFor > TimeSpan.FromMilliseconds(800)))
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        return $"{entry.Summary()}\n{entry.TakeUnread()}";
    }

    public string Stop(int id)
    {
        Entry? entry;
        lock (_lock)
            _entries.TryGetValue(id, out entry);
        if (entry is null)
            return $"Error: no background program with id {id}.";
        if (entry.Exited)
            return $"Program {id} had already exited ({entry.Summary()}).";

        CommandTool.StopTree(entry.Process);
        Changed?.Invoke(this, EventArgs.Empty);
        return $"Stopped background program {id}.";
    }

    public void StopAll()
    {
        List<Entry> running;
        lock (_lock)
            running = [.. _entries.Values.Where(e => !e.Exited)];
        foreach (var entry in running)
            CommandTool.StopTree(entry.Process);
    }

    public void Dispose() => StopAll();

    private sealed class Entry(int id, string command, string? name, Process process)
    {
        private readonly Lock _lock = new();
        private readonly List<string> _lines = [];
        private int _read;       // lines already given out
        private long _dropped;   // lines that fell off the front
        private bool _exited;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastOutput = Stopwatch.GetTimestamp();

        /// <summary>How long since the program last printed a line.</summary>
        public TimeSpan QuietFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastOutput));

        public int Id { get; } = id;
        public string Command { get; } = command;
        public string? Name { get; } = name;
        public Process Process { get; } = process;

        public bool Exited
        {
            get
            {
                lock (_lock)
                    return _exited;
            }
        }

        public bool HasUnread
        {
            get
            {
                lock (_lock)
                    return _lines.Count > _read;
            }
        }

        public void MarkExited()
        {
            lock (_lock)
                _exited = true;
        }

        public void Add(string? line)
        {
            if (line is null)
                return;
            Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp());
            lock (_lock)
            {
                _lines.Add(line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line);
                if (_lines.Count > MaxKeptLines)
                {
                    var drop = _lines.Count - MaxKeptLines;
                    _lines.RemoveRange(0, drop);
                    _read = Math.Max(0, _read - drop);
                    _dropped += drop;
                }
            }
        }

        public string TakeUnread()
        {
            lock (_lock)
            {
                var text = new StringBuilder();
                if (_dropped > 0 && _read == 0)
                    text.Append($"…({_dropped:N0} earlier lines dropped)…\n");
                var fresh = _lines.Skip(_read).ToList();
                _read = _lines.Count;
                if (fresh.Count == 0)
                    return "(no new output)";
                // Keep what the model gets small: the last 200 lines of what is new.
                if (fresh.Count > 200)
                {
                    text.Append($"…({fresh.Count - 200:N0} lines left out)…\n");
                    fresh = fresh.Skip(fresh.Count - 200).ToList();
                }
                foreach (var line in fresh)
                    text.Append(line).Append('\n');
                return text.ToString().TrimEnd();
            }
        }

        public string Summary()
        {
            string state;
            try
            {
                state = Exited ? $"exited with code {Process.ExitCode}" : "running";
            }
            catch (InvalidOperationException)
            {
                state = "exited";
            }
            return $"{(Name is null ? Command : $"{Name}: {Command}")} — {state}, {_clock.Elapsed.TotalSeconds:0} s";
        }
    }
}

/// <summary>start_process, process_output and stop_process: Code mode's way to run a server or a watcher alongside the work.</summary>
[SupportedOSPlatform("windows")]
public static class BackgroundTools
{
    public const string StartName = "start_process";
    public const string OutputName = "process_output";

    public static IEnumerable<AITool> Create(Sandbox sandbox, BackgroundProcesses processes, IToolApprover approver, ToolPermissionStore permissions)
    {
        // Asked about like run_command: the user approves what starts; reading and stopping what they approved needs no question.
        yield return new ApprovalGatedFunction(
            AIFunctionFactory.Create(
                (
                    [Description("The PowerShell command to start (a dev server, a test watcher).")] string command,
                    [Description("A short name for it, to tell it apart in the list.")] string? name = null) =>
                    processes.Start(sandbox.Root, command, name),
                StartName,
                "Start a program that keeps running in the background, in the sandbox folder (a dev server, a watcher), after the user approves it. It returns an id at once; " +
                "read its output with process_output and stop it with stop_process when you are done. Use run_command instead for anything that finishes by itself."),
            CommandTool.ServerName, StartName, readOnly: false, approver, permissions,
            scope: arguments => arguments.TryGetValue("command", out var command) ? CommandTool.AlwaysAllowPrefix(command?.ToString()) : null);

        yield return AIFunctionFactory.Create(
            async (
                [Description("The id start_process returned. Leave out to list every background program.")] int? id,
                CancellationToken cancellationToken,
                [Description("Seconds to wait for new output first (0 to 60), e.g. while a server starts.")] int wait_seconds = 0) =>
                await processes.ReadAsync(id, wait_seconds, cancellationToken).ConfigureAwait(false),
            OutputName,
            "Read what a background program printed since you last looked, and whether it is still running; without an id, list them all.");

        yield return AIFunctionFactory.Create(
            ([Description("The id start_process returned.")] int id) => processes.Stop(id),
            "stop_process",
            "Stop a background program you started (its whole process tree). Stop what you started when you are finished with it.");
    }
}
