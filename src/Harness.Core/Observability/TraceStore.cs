// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Core.Config;

namespace Harness.Core.Observability;

/// <summary>One recorded span: searchable metadata plus, separately, its message content.</summary>
/// <param name="Attributes">Every non-content attribute, as recorded.</param>
/// <param name="Content">Content attributes (prompts, replies, tool arguments and results, HTTP bodies) — loaded only by <see cref="TraceStore.Trace"/>.</param>
public sealed record SpanRecord(
    string SpanId, string TraceId, string? ParentId, string Operation, string Name,
    DateTimeOffset StartedAt, double DurationMs, bool Failed, string? Status,
    string? ConversationId, string? Model, long? InputTokens, long? OutputTokens, long? CachedTokens, long? ReasoningTokens,
    double? TimeToFirstChunkMs, double? Cost, string? Currency, string? ToolName, string? ToolSource,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyDictionary<string, string>? Content = null);

/// <summary>A turn (one user message and the agent's work on it) as the list shows it.</summary>
public sealed record TurnSummary(
    string TraceId, DateTimeOffset StartedAt, double DurationMs, bool Failed, string? Status, string? UserMessage,
    string? ConversationId, string? Model, int ModelCalls, int ToolCalls, string? ToolNames,
    long InputTokens, long OutputTokens, double? Cost, string? Currency);

/// <summary>
/// Recorded spans in trace.db in the data folder. Message content is gzip-compressed in its own column
/// (it is most of the size: every model call carries the conversation so far). Old traces are removed
/// after the retention period.
/// </summary>
public sealed class TraceStore
{
    private readonly string _connectionString;

    public TraceStore(string? path = null)
    {
        path ??= AppPaths.Combine("trace.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS spans (
                span_id          TEXT PRIMARY KEY,
                trace_id         TEXT NOT NULL,
                parent_id        TEXT,
                operation        TEXT NOT NULL,
                name             TEXT NOT NULL,
                started_at       TEXT NOT NULL,
                duration_ms      REAL NOT NULL,
                failed           INTEGER NOT NULL,
                status           TEXT,
                conversation_id  TEXT,
                model            TEXT,
                input_tokens     INTEGER,
                output_tokens    INTEGER,
                cached_tokens    INTEGER,
                reasoning_tokens INTEGER,
                ttft_ms          REAL,
                cost             REAL,
                currency         TEXT,
                tool_name        TEXT,
                tool_source      TEXT,
                title            TEXT,
                attributes       TEXT NOT NULL,
                content          BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_spans_trace ON spans (trace_id);
            CREATE INDEX IF NOT EXISTS ix_spans_started ON spans (operation, started_at);
            """;
        command.ExecuteNonQuery();
    }

    public void Insert(SpanRecord span)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO spans VALUES (
                $span, $trace, $parent, $op, $name, $started, $duration, $failed, $status, $conversation, $model,
                $input, $output, $cached, $reasoning, $ttft, $cost, $currency, $tool, $source, $title, $attributes, $content)
            """;
        var p = command.Parameters;
        p.AddWithValue("$span", span.SpanId);
        p.AddWithValue("$trace", span.TraceId);
        p.AddWithValue("$parent", (object?)span.ParentId ?? DBNull.Value);
        p.AddWithValue("$op", span.Operation);
        p.AddWithValue("$name", span.Name);
        p.AddWithValue("$started", span.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        p.AddWithValue("$duration", span.DurationMs);
        p.AddWithValue("$failed", span.Failed ? 1 : 0);
        p.AddWithValue("$status", (object?)span.Status ?? DBNull.Value);
        p.AddWithValue("$conversation", (object?)span.ConversationId ?? DBNull.Value);
        p.AddWithValue("$model", (object?)span.Model ?? DBNull.Value);
        p.AddWithValue("$input", (object?)span.InputTokens ?? DBNull.Value);
        p.AddWithValue("$output", (object?)span.OutputTokens ?? DBNull.Value);
        p.AddWithValue("$cached", (object?)span.CachedTokens ?? DBNull.Value);
        p.AddWithValue("$reasoning", (object?)span.ReasoningTokens ?? DBNull.Value);
        p.AddWithValue("$ttft", (object?)span.TimeToFirstChunkMs ?? DBNull.Value);
        p.AddWithValue("$cost", (object?)span.Cost ?? DBNull.Value);
        p.AddWithValue("$currency", (object?)span.Currency ?? DBNull.Value);
        p.AddWithValue("$tool", (object?)span.ToolName ?? DBNull.Value);
        p.AddWithValue("$source", (object?)span.ToolSource ?? DBNull.Value);
        p.AddWithValue("$title", span.Content?.TryGetValue(Telemetry.TagUserMessage, out var title) == true ? Shorten(title, 300) : DBNull.Value);
        p.AddWithValue("$attributes", JsonSerializer.Serialize(span.Attributes));
        p.AddWithValue("$content", span.Content is { Count: > 0 } content ? Compress(JsonSerializer.Serialize(content)) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Turns since <paramref name="since"/>, newest first, with their model and tool calls totalled.
    /// <paramref name="search"/> matches the user's message, a tool name or the model.
    /// </summary>
    public IReadOnlyList<TurnSummary> Turns(DateTimeOffset since, string? search = null, bool failedOnly = false, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.trace_id, t.started_at, t.duration_ms, t.failed, t.status, t.title, t.conversation_id,
                   (SELECT c.model FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'chat' AND c.model IS NOT NULL ORDER BY c.started_at DESC LIMIT 1),
                   (SELECT COUNT(*) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'chat'),
                   (SELECT COUNT(*) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'execute_tool'),
                   (SELECT GROUP_CONCAT(DISTINCT c.tool_name) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'execute_tool'),
                   (SELECT TOTAL(c.input_tokens) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'chat'),
                   (SELECT TOTAL(c.output_tokens) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'chat'),
                   (SELECT SUM(c.cost) FROM spans c WHERE c.trace_id = t.trace_id AND c.operation = 'chat'),
                   (SELECT c.currency FROM spans c WHERE c.trace_id = t.trace_id AND c.currency IS NOT NULL LIMIT 1)
            FROM spans t
            WHERE t.operation = 'turn' AND t.started_at >= $since
              {(failedOnly ? "AND (t.failed = 1 OR EXISTS (SELECT 1 FROM spans c WHERE c.trace_id = t.trace_id AND c.failed = 1 AND COALESCE(c.status, '') <> 'cancelled'))" : "")}
              {(string.IsNullOrWhiteSpace(search) ? "" : """
                  AND (t.title LIKE $search ESCAPE '\' OR EXISTS (SELECT 1 FROM spans c WHERE c.trace_id = t.trace_id
                       AND (c.tool_name LIKE $search ESCAPE '\' OR c.model LIKE $search ESCAPE '\')))
                  """)}
            ORDER BY t.started_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);
        if (!string.IsNullOrWhiteSpace(search))
            command.Parameters.AddWithValue("$search", "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");

        var turns = new List<TurnSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            turns.Add(new TurnSummary(
                reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetDouble(2), reader.GetInt64(3) != 0,
                Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7),
                reader.GetInt32(8), reader.GetInt32(9), Text(reader, 10),
                (long)reader.GetDouble(11), (long)reader.GetDouble(12),
                reader.IsDBNull(13) ? null : reader.GetDouble(13), Text(reader, 14)));
        }
        return turns;
    }

    /// <summary>Every span of one turn, with content, in start order.</summary>
    public IReadOnlyList<SpanRecord> Trace(string traceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM spans WHERE trace_id = $trace ORDER BY started_at, rowid";
        command.Parameters.AddWithValue("$trace", traceId);
        var spans = new List<SpanRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.IsDBNull(22) ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(Decompress((byte[])reader.GetValue(22)));
            spans.Add(new SpanRecord(
                reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetString(3), reader.GetString(4),
                ParseTime(reader.GetString(5)), reader.GetDouble(6), reader.GetInt64(7) != 0, Text(reader, 8),
                Text(reader, 9), Text(reader, 10), Long(reader, 11), Long(reader, 12), Long(reader, 13), Long(reader, 14),
                reader.IsDBNull(15) ? null : reader.GetDouble(15), reader.IsDBNull(16) ? null : reader.GetDouble(16), Text(reader, 17),
                Text(reader, 18), Text(reader, 19),
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(21)) ?? [], content));
        }
        return spans;
    }

    /// <summary>Removes traces older than <paramref name="days"/> days.</summary>
    public void Purge(int days)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM spans WHERE trace_id IN (SELECT trace_id FROM spans WHERE operation = 'turn' AND started_at < $before);
            DELETE FROM spans WHERE started_at < $before;
            """;
        command.Parameters.AddWithValue("$before", DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes every recorded trace and gives the space back.</summary>
    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM spans; VACUUM;";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static long? Long(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max];

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static string Decompress(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
