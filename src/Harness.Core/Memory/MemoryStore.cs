// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Config;

namespace Harness.Core.Memory;

/// <summary>
/// The agent's long-term memory: one small Markdown file per memory in the <c>memory</c> folder of the data folder
/// (see <see cref="AppPaths"/>), with a few lines of front matter (type, scope, where it came from, how often it was
/// used) above the text. The files are the only copy: the user can read, edit or delete them by hand. Changed and
/// forgotten memories are kept in <c>memory/.history</c> so a mistake can be undone.
/// </summary>
public sealed partial class MemoryStore
{
    /// <summary>Longest memory text accepted.</summary>
    public const int MaxTextLength = 600;

    // No byte order mark: plain Markdown that any editor reads the same.
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;
    private readonly Lock _lock = new();

    public MemoryStore(string? folder = null)
    {
        _folder = folder ?? AppPaths.Combine("memory");
        Directory.CreateDirectory(_folder);
    }

    public string Folder => _folder;

    private string HistoryFolder => Path.Combine(_folder, ".history");

    private string PathOf(string id) => Path.Combine(_folder, id + ".md");

    #region Reading

    /// <summary>Every memory, most recently changed first. A file that can't be read is skipped.</summary>
    public IReadOnlyList<MemoryItem> All()
    {
        var items = new List<MemoryItem>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file)) is { } item)
                    items.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being written or locked right now: it shows up next time.
            }
        }
        return [.. items.OrderByDescending(i => i.Updated)];
    }

    public MemoryItem? Get(string id) =>
        IsValidId(id) && File.Exists(PathOf(id)) ? Parse(File.ReadAllText(PathOf(id)), id) : null;

    /// <summary>True when nothing has been used for longer than <paramref name="archiveDays"/> days (0 turns archiving off).</summary>
    public static bool IsArchived(MemoryItem item, int archiveDays, DateTimeOffset? now = null)
    {
        if (archiveDays <= 0)
            return false;
        var last = item.LastUsed is { } used && used > item.Updated ? used : item.Updated;
        return (now ?? DateTimeOffset.UtcNow) - last > TimeSpan.FromDays(archiveDays);
    }

    private static bool InScope(MemoryItem item, string? sandboxRoot) =>
        item.SandboxFolder is not { } folder
        || (sandboxRoot is not null && string.Equals(NormalizePath(folder), NormalizePath(sandboxRoot), StringComparison.OrdinalIgnoreCase));

    private static string NormalizePath(string path) => path.Trim().TrimEnd('\\', '/');

    /// <summary>
    /// Memories that match <paramref name="query"/> (any of its words), best first: memories that match more words, are
    /// settled, or are used often rank higher. An empty query lists the most recently changed.
    /// </summary>
    public IReadOnlyList<MemoryItem> Search(string? query, string? sandboxRoot, int limit = 8, int archiveDays = 0, bool includeArchived = true)
    {
        var words = Words(query);
        var candidates = All().Where(i => InScope(i, sandboxRoot) && (includeArchived || !IsArchived(i, archiveDays)));
        if (words.Count == 0)
            return [.. candidates.Take(Math.Clamp(limit, 1, 50))];

        return [.. candidates
            .Select(i => (Item: i, Score: Relevance(i, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.Updated)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(x => x.Item)];
    }

    /// <summary>
    /// The memories to put in front of the model at the start of a conversation: global ones and those of the current
    /// sandbox folder that aren't archived, the ones that match <paramref name="seed"/> (the first message) and the settled
    /// or often used ones first, until <paramref name="budgetChars"/> is used up.
    /// </summary>
    public IReadOnlyList<MemoryItem> Select(string? sandboxRoot, string? seed, int budgetChars, int archiveDays)
    {
        if (budgetChars <= 0)
            return [];

        var words = Words(seed);
        var picked = new List<MemoryItem>();
        var used = 0;
        foreach (var item in All()
            .Where(i => InScope(i, sandboxRoot) && !IsArchived(i, archiveDays))
            .OrderByDescending(i => Relevance(i, words) + Weight(i)))
        {
            var cost = item.Text.Length + 24;
            if (used + cost > budgetChars)
                continue;
            picked.Add(item);
            used += cost;
        }
        return picked;
    }

    private static double Weight(MemoryItem item) =>
        (item.Status == MemoryStatus.Settled ? 2 : 0) + Math.Log(1 + item.Uses);

    private static double Relevance(MemoryItem item, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return 0;
        var hits = words.Count(w => item.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
        return hits * 5 + (hits > 0 ? Weight(item) : 0);
    }

    /// <summary>The words of a query. Chinese has no spaces, so every run of Han characters also contributes its two-character pieces.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var words = new List<string>();
        foreach (Match token in WordPattern().Matches(text))
        {
            var value = token.Value;
            if (value.Any(IsHan))
            {
                if (value.Length <= 2)
                    words.Add(value);
                else
                    for (var i = 0; i + 2 <= value.Length; i++)
                        words.Add(value.Substring(i, 2));
            }
            else if (value.Length >= 2)
            {
                words.Add(value);
            }
        }
        return [.. words.Distinct(StringComparer.OrdinalIgnoreCase).Take(24)];
    }

    private static bool IsHan(char c) => c is >= '一' and <= '鿿';

    [GeneratedRegex(@"[\p{L}\p{N}_\-]+")]
    private static partial Regex WordPattern();

    #endregion

    #region Writing

    /// <summary>Saves a new memory, or throws <see cref="ArgumentException"/> (with a message fit to show) when the text isn't acceptable.</summary>
    public MemoryItem Add(
        string text, MemoryType type, string scope, string origin, IEnumerable<MemorySource>? sources = null,
        MemoryStatus status = MemoryStatus.New)
    {
        text = Check(text);
        var now = DateTimeOffset.UtcNow;
        var item = new MemoryItem(
            NewId(), type, NormalizeScope(scope), status, origin, now, now, null, 0,
            [.. sources ?? []], [], text);
        lock (_lock)
            Write(item);
        return item;
    }

    /// <summary>Changes a memory (the old version is kept in .history). Returns the new version, or null if there is no such memory.</summary>
    public MemoryItem? Update(string id, Func<MemoryItem, MemoryItem> change)
    {
        lock (_lock)
        {
            if (Get(id) is not { } old)
                return null;

            var changed = change(old) with { Id = old.Id, Updated = old.Updated };
            if (changed.Text != old.Text)
                changed = changed with { Text = Check(changed.Text) };
            if (changed == old)
                return old;

            changed = changed with { Updated = DateTimeOffset.UtcNow };
            Snapshot(old);
            Write(changed);
            return changed;
        }
    }

    public MemoryItem? SetText(string id, string text) => Update(id, i => i with { Text = text });

    /// <summary>Takes a memory away (its file moves to .history). False if there is no such memory.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (Get(id) is not { } item)
                return false;

            Snapshot(item);
            File.Delete(PathOf(id));

            // Nobody should point at what is gone.
            foreach (var other in All().Where(o => o.Related.Contains(id)))
                Write(other with { Related = [.. other.Related.Where(r => r != id)] });
            return true;
        }
    }

    /// <summary>Counts a use of each of these memories (they were put in front of the model).</summary>
    public void Touch(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in ids.Distinct())
            {
                if (Get(id) is { } item)
                    Write(item with { Uses = item.Uses + 1, LastUsed = now });
            }
        }
    }

    /// <summary>Marks two memories as related, in both directions.</summary>
    public void Link(string a, string b)
    {
        if (a == b)
            return;
        lock (_lock)
        {
            if (Get(a) is not { } first || Get(b) is not { } second)
                return;
            if (!first.Related.Contains(b))
                Write(first with { Related = [.. first.Related, b] });
            if (!second.Related.Contains(a))
                Write(second with { Related = [.. second.Related, a] });
        }
    }

    /// <summary>Puts back the last saved earlier version of a memory (also one that was forgotten).</summary>
    public MemoryItem? Restore(string id)
    {
        lock (_lock)
        {
            if (!Directory.Exists(HistoryFolder))
                return null;
            var last = Directory.EnumerateFiles(HistoryFolder, id + "-*.md").OrderDescending().FirstOrDefault();
            if (last is null || Parse(File.ReadAllText(last), id) is not { } item)
                return null;
            if (Get(id) is { } current)
                Snapshot(current);
            var restored = item with { Updated = DateTimeOffset.UtcNow };
            Write(restored);
            return restored;
        }
    }

    private void Snapshot(MemoryItem item)
    {
        Directory.CreateDirectory(HistoryFolder);
        File.WriteAllText(
            Path.Combine(HistoryFolder, $"{item.Id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.md"), Format(item), s_utf8);
    }

    private void Write(MemoryItem item)
    {
        // Through a temporary file, so a crash never leaves half a memory.
        var path = PathOf(item.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Format(item), s_utf8);
        File.Move(temp, path, overwrite: true);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope.Trim(), MemoryItem.GlobalScope, StringComparison.OrdinalIgnoreCase))
            return MemoryItem.GlobalScope;
        return scope.StartsWith("sandbox:", StringComparison.OrdinalIgnoreCase) ? "sandbox:" + scope["sandbox:".Length..].Trim() : MemoryItem.GlobalScope;
    }

    #endregion

    #region What may be remembered

    /// <summary>The text trimmed, or <see cref="ArgumentException"/> when it is empty, too long or looks like a secret.</summary>
    public static string Check(string? text)
    {
        var trimmed = text?.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("There is nothing to remember: the text is empty.");
        if (trimmed.Length > MaxTextLength)
            throw new ArgumentException($"A memory is a short note (at most {MaxTextLength} characters); this one is {trimmed.Length}. Shorten it or split it.");
        if (LooksSecret(trimmed))
            throw new ArgumentException("This looks like it contains a key, token or password. Those are never saved as memories.");
        return trimmed;
    }

    /// <summary>True for text that appears to hold a credential: API keys, private keys, "password: …", connection strings.</summary>
    public static bool LooksSecret(string text) => SecretPattern().IsMatch(text);

    [GeneratedRegex(
        @"(sk-[A-Za-z0-9_\-]{16,})|(AKIA[0-9A-Z]{16})|(ghp_[A-Za-z0-9]{20,})|(xox[baprs]-[A-Za-z0-9\-]{10,})|(-----BEGIN [A-Z ]*PRIVATE KEY-----)|(eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})|((password|passwd|pwd|secret|api[_\- ]?key|token|密碼|金鑰|密鑰)\s*[:=：是為]\s*\S{6,})|((Server|Data Source|Host)=[^;]+;.*(Password|Pwd)=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    #endregion

    #region Files

    private static string Format(MemoryItem item)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(item.Id).Append('\n');
        sb.Append("type: ").Append(item.Type.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("scope: ").Append(item.Scope).Append('\n');
        sb.Append("status: ").Append(item.Status.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("origin: ").Append(item.Origin).Append('\n');
        sb.Append("created: ").Append(Stamp(item.Created)).Append('\n');
        sb.Append("updated: ").Append(Stamp(item.Updated)).Append('\n');
        sb.Append("last_used: ").Append(item.LastUsed is { } used ? Stamp(used) : string.Empty).Append('\n');
        sb.Append("uses: ").Append(item.Uses.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("sources: ").Append(string.Join(", ", item.Sources.Select(s => $"{s.ConversationId}#{s.Seq}"))).Append('\n');
        sb.Append("related: ").Append(string.Join(", ", item.Related)).Append('\n');
        sb.Append("---\n");
        sb.Append(item.Text).Append('\n');
        return sb.ToString();
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Reads a memory file; null when it isn't one. Missing or odd lines fall back to defaults, so a hand-written file works.</summary>
    internal static MemoryItem? Parse(string content, string fileId)
    {
        var text = content.Replace("\r\n", "\n").TrimStart('﻿');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                body = text[(end + 4)..].TrimStart('\n');
            }
        }

        body = body.Trim();
        if (body.Length == 0)
            return null;

        var id = fields.GetValueOrDefault("id") is { Length: > 0 } declared && IsValidId(declared) ? declared : fileId;
        var created = Time(fields.GetValueOrDefault("created")) ?? DateTimeOffset.UtcNow;
        return new MemoryItem(
            id,
            Enum.TryParse<MemoryType>(fields.GetValueOrDefault("type"), ignoreCase: true, out var type) ? type : MemoryType.Fact,
            NormalizeScope(fields.GetValueOrDefault("scope")),
            Enum.TryParse<MemoryStatus>(fields.GetValueOrDefault("status"), ignoreCase: true, out var status) ? status : MemoryStatus.New,
            fields.GetValueOrDefault("origin") is { Length: > 0 } origin ? origin : "user",
            created,
            Time(fields.GetValueOrDefault("updated")) ?? created,
            Time(fields.GetValueOrDefault("last_used")),
            int.TryParse(fields.GetValueOrDefault("uses"), NumberStyles.None, CultureInfo.InvariantCulture, out var uses) ? uses : 0,
            [.. Split(fields.GetValueOrDefault("sources")).Select(ParseSource).OfType<MemorySource>()],
            [.. Split(fields.GetValueOrDefault("related")).Where(IsValidId)],
            body);
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static MemorySource? ParseSource(string text)
    {
        var hash = text.IndexOf('#');
        var id = hash < 0 ? text : text[..hash];
        if (!IsValidId(id))
            return null;
        _ = long.TryParse(hash < 0 ? null : text[(hash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var seq);
        return new MemorySource(id, seq);
    }

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

    #endregion

    #region State

    private string StatePath => Path.Combine(_folder, ".state.json");

    /// <summary>When memories were last consolidated (conversations after that are what the next consolidation looks at); null if never.</summary>
    public DateTimeOffset? LastConsolidated
    {
        get
        {
            try
            {
                return File.Exists(StatePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) is { } state ? state.LastConsolidated : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void MarkConsolidated(DateTimeOffset when)
    {
        lock (_lock)
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(when)));
    }

    private sealed record State(DateTimeOffset LastConsolidated);

    #endregion
}
