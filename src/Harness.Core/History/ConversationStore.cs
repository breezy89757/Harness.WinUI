// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Core.Config;

namespace Harness.Core.History;

/// <summary>One tool call or reasoning block as it should be replayed in the transcript.</summary>
/// <param name="Arguments">Tool arguments as JSON.</param>
/// <param name="State">running / waiting / done / error — as the step last appeared.</param>
/// <param name="Result">Tool result as JSON (or a JSON string with a text summary for large results).</param>
public sealed record StoredStep(string Id, string Name, string? Arguments, string State, string? Result = null, string? Note = null);

/// <param name="Text">Raw text as the user typed it or the model wrote it (Markdown, artifact tags included).</param>
public sealed record StoredMessage(
    string Role,
    string Text,
    string? Reasoning = null,
    IReadOnlyList<StoredStep>? Steps = null,
    string? Meta = null,
    string? MetaTooltip = null,
    string? TraceId = null);

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>One message that matched a search: where it is (conversation and position) and a piece of its text around the match.</summary>
/// <param name="Seq">The message's position in its conversation, counting from 1.</param>
public sealed record MessageHit(string ConversationId, string Title, long Seq, string Role, DateTimeOffset At, string Snippet);

/// <param name="SessionState">Serialized agent session, so the conversation can continue where it left off.</param>
public sealed record StoredConversation(string Id, string Title, IReadOnlyList<StoredMessage> Messages, string? SessionState);

/// <summary>
/// Conversation history in a local SQLite database (history.db in the data folder, see <see cref="AppPaths"/>). Message text
/// is kept in its own column so full-text search can be added later; everything needed only to
/// replay the transcript (reasoning, tool steps, footer) lives in a JSON <c>details</c> column.
/// </summary>
public sealed class ConversationStore
{
    private static readonly JsonSerializerOptions s_json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly string _connectionString;

    // Databases whose tables have been checked in this run (with when the file was created); a store made again for the same file (the
    // memory tools do) skips that, unless the file is gone or was replaced.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> s_ready = new(StringComparer.OrdinalIgnoreCase);

    public ConversationStore(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        if (File.Exists(path) && s_ready.TryGetValue(path, out var created) && created == File.GetCreationTimeUtc(path))
            return;

        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS conversations (
                id            TEXT PRIMARY KEY,
                title         TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                session_state TEXT
            );
            CREATE TABLE IF NOT EXISTS messages (
                conversation_id TEXT    NOT NULL,
                seq             INTEGER NOT NULL,
                role            TEXT    NOT NULL,
                text            TEXT    NOT NULL,
                details         TEXT,
                created_at      TEXT    NOT NULL,
                PRIMARY KEY (conversation_id, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations (updated_at DESC);
            """);

        // Code mode (1.2): which mode and project folder a conversation belongs to. Existing rows keep NULL, meaning chat.
        if (!HasColumn(connection, "conversations", "mode"))
            Execute(connection, "ALTER TABLE conversations ADD COLUMN mode TEXT; ALTER TABLE conversations ADD COLUMN workspace TEXT;");
        // The agent's task list (Code mode), as JSON, so a reopened conversation shows it again.
        if (!HasColumn(connection, "conversations", "tasks"))
            Execute(connection, "ALTER TABLE conversations ADD COLUMN tasks TEXT;");
        s_ready[path] = File.GetCreationTimeUtc(path);
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        command.Parameters.AddWithValue("$name", column);
        return (long)command.ExecuteScalar()! > 0;
    }

    public static string DefaultPath => AppPaths.Combine("history.db");

    /// <param name="search">Only conversations whose title or any message contains this text (case-insensitive for ASCII).</param>
    /// <param name="mode">"chat" (conversations from before Code mode count as chat), "code", or null for every conversation.</param>
    /// <param name="workspace">With mode "code": only conversations about this project folder; null for all of them.</param>
    public IReadOnlyList<ConversationSummary> List(int limit = 100, string? search = null, string? mode = null, string? workspace = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$limit", limit);

        var filters = new List<string>();
        if (mode == "code")
        {
            filters.Add("mode = 'code'");
            if (workspace is not null)
            {
                filters.Add("workspace = $workspace");
                command.Parameters.AddWithValue("$workspace", workspace);
            }
        }
        else if (mode == "chat")
        {
            filters.Add("(mode IS NULL OR mode <> 'code')");
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            command.CommandText = $"SELECT id, title, updated_at FROM conversations {Where(filters)} ORDER BY updated_at DESC LIMIT $limit";
        }
        else
        {
            // LIKE wildcards in what the user typed are literal characters, not patterns.
            filters.Add("(title LIKE $pattern ESCAPE '\\' OR id IN (SELECT conversation_id FROM messages WHERE text LIKE $pattern ESCAPE '\\'))");
            command.CommandText = $"SELECT id, title, updated_at FROM conversations {Where(filters)} ORDER BY updated_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$pattern", "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }

        var results = new List<ConversationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ConversationSummary(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        return results;
    }

    private static string Where(List<string> filters) => filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

    /// <summary>
    /// Messages (what the user wrote and what the model answered, not tool output) that contain every word of
    /// <paramref name="query"/>, newest first, each with a snippet around the first word found.
    /// </summary>
    /// <param name="excludeConversationId">A conversation to leave out, e.g. the one the search is made from.</param>
    public IReadOnlyList<MessageHit> Search(
        string query, DateTimeOffset? since = null, DateTimeOffset? until = null, string? excludeConversationId = null, int limit = 10)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        if (words.Count == 0)
            return [];

        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string> { "m.role IN ('user', 'assistant')" };
        for (var i = 0; i < words.Count; i++)
        {
            // LIKE wildcards in what was typed are literal characters, not patterns.
            where.Add($"m.text LIKE $w{i} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$w{i}", "%" + words[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (since is { } from)
        {
            where.Add("m.created_at >= $since");
            command.Parameters.AddWithValue("$since", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        if (until is { } to)
        {
            where.Add("m.created_at < $until");
            command.Parameters.AddWithValue("$until", to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        if (excludeConversationId is not null)
        {
            where.Add("m.conversation_id <> $exclude");
            command.Parameters.AddWithValue("$exclude", excludeConversationId);
        }
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        command.CommandText = $"""
            SELECT m.conversation_id, c.title, m.seq, m.role, m.created_at, m.text
            FROM messages m JOIN conversations c ON c.id = m.conversation_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY m.created_at DESC, m.seq DESC LIMIT $limit
            """;

        var hits = new List<MessageHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new MessageHit(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                ParseTime(reader.GetString(4)), Snippet(reader.GetString(5), words)));
        }
        return hits;
    }

    /// <summary>About 200 characters of <paramref name="text"/> around the first of <paramref name="words"/> found in it, on one line.</summary>
    private static string Snippet(string text, IReadOnlyList<string> words)
    {
        const int Radius = 100;
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - Radius);
        var end = Math.Min(text.Length, at + Radius);
        var piece = text[start..end].ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : "") + piece + (end < text.Length ? "…" : "");
    }

    /// <summary>The titles and times of these conversations (those that exist).</summary>
    public IReadOnlyList<ConversationSummary> Describe(IEnumerable<string> ids)
    {
        var wanted = ids.Distinct().Take(500).ToList();
        if (wanted.Count == 0)
            return [];

        using var connection = Open();
        using var command = connection.CreateCommand();
        var names = wanted.Select((id, i) => "$i" + i).ToList();
        for (var i = 0; i < wanted.Count; i++)
            command.Parameters.AddWithValue(names[i], wanted[i]);
        command.CommandText = $"SELECT id, title, updated_at FROM conversations WHERE id IN ({string.Join(", ", names)})";

        var results = new List<ConversationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ConversationSummary(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        return results;
    }

    /// <summary>The position the next message appended to this conversation will have (1 for a conversation not saved yet).</summary>
    public long NextSeq(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM messages WHERE conversation_id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()!;
    }

    public StoredConversation? Load(string id)
    {
        using var connection = Open();
        string title;
        string? sessionState;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT title, session_state FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            title = reader.GetString(0);
            sessionState = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var messages = new List<StoredMessage>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT role, text, details FROM messages WHERE conversation_id = $id ORDER BY seq";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var details = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<Details>(reader.GetString(2), s_json);
                messages.Add(new StoredMessage(
                    reader.GetString(0), reader.GetString(1),
                    details?.Reasoning, details?.Steps, details?.Meta, details?.MetaTooltip));
            }
        }

        return new StoredConversation(id, title, messages, sessionState);
    }

    /// <summary>Appends messages (creating the conversation on first use) and replaces its session state.</summary>
    /// <param name="mode">"chat" or "code", recorded when the conversation is created.</param>
    /// <param name="workspace">In Code mode, the project folder the conversation is about.</param>
    public void Append(string id, string title, IEnumerable<StoredMessage> messages, string? sessionState, string? mode = null, string? workspace = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO conversations (id, title, created_at, updated_at, mode, workspace) VALUES ($id, $title, $now, $now, $mode, $workspace)
                ON CONFLICT (id) DO NOTHING;
                UPDATE conversations SET updated_at = $now, session_state = $state WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$mode", (object?)mode ?? DBNull.Value);
            command.Parameters.AddWithValue("$workspace", (object?)workspace ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$state", (object?)sessionState ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        long seq;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE conversation_id = $id";
            command.Parameters.AddWithValue("$id", id);
            seq = (long)command.ExecuteScalar()!;
        }

        foreach (var message in messages)
        {
            var details = message.Reasoning is null && message.Steps is not { Count: > 0 } && message.Meta is null
                ? null
                : JsonSerializer.Serialize(new Details(message.Reasoning, message.Steps, message.Meta, message.MetaTooltip), s_json);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO messages (conversation_id, seq, role, text, details, created_at)
                VALUES ($id, $seq, $role, $text, $details, $now)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$seq", ++seq);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$text", message.Text);
            command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Saves the task list shown with a conversation (null or empty for none).</summary>
    public void SaveTasks(string id, string? tasksJson)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET tasks = $tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$tasks", string.IsNullOrEmpty(tasksJson) ? DBNull.Value : tasksJson);
        command.ExecuteNonQuery();
    }

    /// <summary>The task list saved with a conversation, or null.</summary>
    public string? LoadTasks(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tasks FROM conversations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Changes a conversation's title. Doesn't touch its time, so it keeps its place in the list.</summary>
    public void Rename(string id, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title = $title WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE conversation_id = $id; DELETE FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record Details(string? Reasoning, IReadOnlyList<StoredStep>? Steps, string? Meta, string? MetaTooltip);
}
