// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Core.Config;

namespace Harness.Core.Usage;

/// <summary>A model's prices per million tokens, in the currency the user chose (see <see cref="AppPreferences.Currency"/>).</summary>
/// <param name="CachedInput">For input tokens served from the provider's prompt cache (usually much cheaper).</param>
public sealed record ModelPrice(decimal Input, decimal CachedInput, decimal Output)
{
    /// <summary>Cost of one model call. Cached tokens are part of <paramref name="input"/>.</summary>
    public decimal Cost(long input, long cached, long output)
    {
        cached = Math.Clamp(cached, 0, input);
        return ((input - cached) * Input + cached * CachedInput + output * Output) / 1_000_000m;
    }
}

/// <summary>One reply's token usage and, when the model has a price, its cost.</summary>
public sealed record UsageEntry(
    DateTimeOffset At, string? ConversationId, string? Model, long InputTokens, long CachedInputTokens, long OutputTokens,
    decimal? Cost, string Currency);

/// <param name="Cost">Total cost per currency (normally one).</param>
/// <param name="UnpricedReplies">Replies from models with no price set, so not in <paramref name="Cost"/>.</param>
public sealed record UsageTotals(int Replies, long InputTokens, long OutputTokens, IReadOnlyDictionary<string, decimal> Cost, int UnpricedReplies)
{
    public static UsageTotals Empty { get; } = new(0, 0, 0, new Dictionary<string, decimal>(), 0);
}

/// <summary>
/// Every reply's token usage and cost, in usage.db in the data folder (its own database, so it keeps
/// working when the conversation history can't be opened). The cost is stored as computed at the
/// time, so changing prices later doesn't rewrite past spending.
/// </summary>
public sealed class UsageLedger
{
    private readonly string _connectionString;

    public UsageLedger(string? path = null)
    {
        path ??= AppPaths.Combine("usage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS usage (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                at              TEXT    NOT NULL,
                conversation_id TEXT,
                model           TEXT,
                input_tokens    INTEGER NOT NULL,
                cached_tokens   INTEGER NOT NULL,
                output_tokens   INTEGER NOT NULL,
                cost            TEXT,
                currency        TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_usage_at ON usage (at);
            CREATE INDEX IF NOT EXISTS ix_usage_conversation ON usage (conversation_id);
            """;
        command.ExecuteNonQuery();
    }

    public void Record(UsageEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage (at, conversation_id, model, input_tokens, cached_tokens, output_tokens, cost, currency)
            VALUES ($at, $conversation, $model, $input, $cached, $output, $cost, $currency)
            """;
        command.Parameters.AddWithValue("$at", entry.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$conversation", (object?)entry.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)entry.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$input", entry.InputTokens);
        command.Parameters.AddWithValue("$cached", entry.CachedInputTokens);
        command.Parameters.AddWithValue("$output", entry.OutputTokens);
        // decimal as invariant text: no float rounding on tiny per-call amounts.
        command.Parameters.AddWithValue("$cost", (object?)entry.Cost?.ToString(CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$currency", entry.Currency);
        command.ExecuteNonQuery();
    }

    /// <summary>Totals for one conversation, or for everything since <paramref name="since"/> (null = all time).</summary>
    public UsageTotals Totals(string? conversationId = null, DateTimeOffset? since = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (conversationId is not null)
        {
            where.Add("conversation_id = $conversation");
            command.Parameters.AddWithValue("$conversation", conversationId);
        }
        if (since is not null)
        {
            where.Add("at >= $since");
            command.Parameters.AddWithValue("$since", since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        command.CommandText = "SELECT input_tokens, output_tokens, cost, currency FROM usage" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty);

        int replies = 0, unpriced = 0;
        long input = 0, output = 0;
        var cost = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            replies++;
            input += reader.GetInt64(0);
            output += reader.GetInt64(1);
            if (reader.IsDBNull(2) || !decimal.TryParse(reader.GetString(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                unpriced++;
                continue;
            }
            var currency = reader.GetString(3);
            cost[currency] = cost.GetValueOrDefault(currency) + amount;
        }
        return new UsageTotals(replies, input, output, cost, unpriced);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
