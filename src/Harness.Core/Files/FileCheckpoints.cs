// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Config;

namespace Harness.Core.Files;

/// <summary>
/// What the file tools changed during each user message, kept so it can be undone: before a file is written, edited or moved
/// its previous state is saved (in the data folder, <c>checkpoints</c>). Changes made by commands the agent runs are not
/// covered (git is the way back for those). Only the most recent messages are kept.
/// </summary>
public sealed class FileCheckpoints
{
    private const int KeepTurns = 30;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private readonly string _root;
    private readonly Lock _lock = new();
    private string? _turn;
    private readonly Dictionary<string, TurnRecord> _turns = new(StringComparer.Ordinal);

    public FileCheckpoints(string? root = null)
    {
        _root = root ?? AppPaths.Combine("checkpoints");
        RemoveLeftovers();
    }

    /// <summary>Saved copies from an earlier run: the turns they belong to are gone, so they can't be undone any more.</summary>
    private void RemoveLeftovers()
    {
        try
        {
            if (!Directory.Exists(_root))
                return;
            foreach (var file in Directory.EnumerateFiles(_root))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Raised when a turn first records a change (the id of the turn).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>The turn the next changes belong to: one per message the user sends.</summary>
    public void BeginTurn(string turnId)
    {
        lock (_lock)
            _turn = turnId;
    }

    /// <summary>The message is over: later changes (by the app, not by the agent) don't belong to it.</summary>
    public void EndTurn()
    {
        lock (_lock)
            _turn = null;
    }

    /// <summary>Called just before <paramref name="fullPath"/> (an existing file, or one about to be created) is changed.</summary>
    public void BeforeChange(string fullPath)
    {
        string turn;
        TurnRecord record;
        bool first;
        lock (_lock)
        {
            if (_turn is null)
                return;
            turn = _turn;
            if (!_turns.TryGetValue(turn, out record!))
            {
                record = new TurnRecord();
                _turns[turn] = record;
            }
            if (record.Files.ContainsKey(fullPath))
                return; // only the state before the first change in this turn matters
            first = record.Files.Count == 0 && record.Moves.Count == 0;

            var entry = new FileEntry { Existed = File.Exists(fullPath) };
            if (entry.Existed)
            {
                var info = new FileInfo(fullPath);
                if (info.Length > MaxFileBytes)
                {
                    entry.TooLarge = true;
                }
                else
                {
                    Directory.CreateDirectory(_root);
                    entry.Blob = Guid.NewGuid().ToString("N");
                    File.Copy(fullPath, Path.Combine(_root, entry.Blob), overwrite: true);
                }
            }
            record.Files[fullPath] = entry;
            Prune();
        }
        if (first)
            Changed?.Invoke(this, turn);
    }

    /// <summary>A file or folder is about to be moved from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void BeforeMove(string from, string to)
    {
        string turn;
        bool first;
        lock (_lock)
        {
            if (_turn is null)
                return;
            turn = _turn;
            if (!_turns.TryGetValue(turn, out var record))
                _turns[turn] = record = new TurnRecord();
            first = record.Files.Count == 0 && record.Moves.Count == 0;
            record.Moves.Add((from, to));
        }
        if (first)
            Changed?.Invoke(this, turn);
    }

    /// <summary>The files a turn changed, as the paths they have now (empty if it changed none).</summary>
    public IReadOnlyList<string> ChangedFiles(string turnId)
    {
        lock (_lock)
        {
            if (!_turns.TryGetValue(turnId, out var record))
                return [];
            return [.. record.Files.Keys.Concat(record.Moves.Select(m => m.To)).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Puts back what a turn changed: files return to how they were, files it created are removed, moves are undone. Returns what could not be restored.</summary>
    public IReadOnlyList<string> Undo(string turnId)
    {
        TurnRecord? record;
        lock (_lock)
        {
            if (!_turns.TryGetValue(turnId, out record))
                return [];
        }

        var problems = new List<string>();
        // Newest change first, so moves unwind before the files they moved.
        foreach (var (from, to) in Enumerable.Reverse(record.Moves))
        {
            try
            {
                if (Directory.Exists(to) && !Directory.Exists(from) && !File.Exists(from))
                    Directory.Move(to, from);
                else if (File.Exists(to) && !File.Exists(from) && !Directory.Exists(from))
                    File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{to}: {ex.Message}");
            }
        }

        foreach (var (path, entry) in record.Files)
        {
            try
            {
                if (!entry.Existed)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else if (entry.Blob is { } blob && File.Exists(Path.Combine(_root, blob)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(Path.Combine(_root, blob), path, overwrite: true);
                }
                else
                {
                    problems.Add($"{path}: the earlier version wasn't kept{(entry.TooLarge ? " (too large)" : "")}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{path}: {ex.Message}");
            }
        }

        lock (_lock)
            _turns.Remove(turnId);
        foreach (var entry in record.Files.Values.Where(e => e.Blob is not null))
            TryDelete(Path.Combine(_root, entry.Blob!));
        return problems;
    }

    private void Prune()
    {
        // Called with the lock held. Keep the newest turns, drop the saved copies of the rest.
        if (_turns.Count <= KeepTurns)
            return;
        foreach (var old in _turns.Keys.Take(_turns.Count - KeepTurns).ToList())
        {
            foreach (var entry in _turns[old].Files.Values.Where(e => e.Blob is not null))
                TryDelete(Path.Combine(_root, entry.Blob!));
            _turns.Remove(old);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class TurnRecord
    {
        public Dictionary<string, FileEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string From, string To)> Moves { get; } = [];
    }

    private sealed class FileEntry
    {
        public bool Existed { get; init; }
        public string? Blob { get; set; }
        public bool TooLarge { get; set; }
    }
}
