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

    public ConversationStore(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

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
    }

    public static string DefaultPath => AppPaths.Combine("history.db");

    public IReadOnlyList<ConversationSummary> List(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, updated_at FROM conversations ORDER BY updated_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ConversationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ConversationSummary(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        return results;
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
    public void Append(string id, string title, IEnumerable<StoredMessage> messages, string? sessionState)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $now, $now)
                ON CONFLICT (id) DO NOTHING;
                UPDATE conversations SET updated_at = $now, session_state = $state WHERE id = $id;
                """;
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
