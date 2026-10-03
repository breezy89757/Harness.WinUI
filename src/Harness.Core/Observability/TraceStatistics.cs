// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Harness.Core.Observability;

public sealed record TraceTotals(
    int Turns, int FailedTurns, int ModelCalls, int ToolCalls, long InputTokens, long OutputTokens,
    double? Cost, string? Currency, double AverageTurnMs);

/// <param name="P50Ms">Median duration of a call.</param>
/// <param name="P95Ms">95th percentile duration: how slow the slow calls are.</param>
/// <param name="AverageToolDefinitionsChars">Average size of the tool definitions sent with each call (they cost input tokens every time).</param>
public sealed record ModelStatistics(
    string Model, int Calls, int Failures, double P50Ms, double P95Ms, double? AverageTtftMs,
    long InputTokens, long OutputTokens, double? Cost, double AverageToolDefinitionsChars);

/// <param name="Offered">Model calls that were offered this tool.</param>
/// <param name="AverageResultChars">Average size of what the tool returned to the model (it all goes into the context).</param>
public sealed record ToolStatistics(
    string Name, string Source, int Offered, int Calls, int Failures, double AverageMs, double MaxMs, double? AverageResultChars);

public sealed record SkillStatistics(string Name, int Loads, int FileReads, DateTimeOffset LastUsed);

public sealed record ApprovalStatistics(string Decision, int Count, double AverageWaitMs);

public sealed record TraceStatistics(
    TraceTotals Totals, IReadOnlyList<ModelStatistics> Models, IReadOnlyList<ToolStatistics> Tools,
    IReadOnlyList<SkillStatistics> Skills, IReadOnlyList<ApprovalStatistics> Approvals);

public sealed partial class TraceStore
{
    /// <summary>Totals per model, tool, skill and approval decision since <paramref name="since"/>.</summary>
    public TraceStatistics Statistics(DateTimeOffset since)
    {
        var rows = new List<Row>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT operation, model, duration_ms, failed, status, ttft_ms, input_tokens, output_tokens, cost, currency,
                       tool_name, tool_source, attributes, started_at
                FROM spans
                WHERE started_at >= $since AND operation IN ('turn', 'chat', 'execute_tool', 'approval')
                """;
            command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Row(
                    reader.GetString(0), Text(reader, 1), reader.GetDouble(2),
                    reader.GetInt64(3) != 0 && Text(reader, 4) != Telemetry.StatusCancelled,
                    reader.IsDBNull(5) ? null : reader.GetDouble(5), Long(reader, 6) ?? 0, Long(reader, 7) ?? 0,
                    reader.IsDBNull(8) ? null : reader.GetDouble(8), Text(reader, 9), Text(reader, 10), Text(reader, 11),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(12)) ?? [], ParseTime(reader.GetString(13))));
            }
        }

        var turns = rows.Where(r => r.Operation == Telemetry.OpTurn).ToList();
        var chats = rows.Where(r => r.Operation == Telemetry.OpChat).ToList();
        var tools = rows.Where(r => r.Operation == Telemetry.OpTool && r.ToolName is not null).ToList();
        var currency = chats.FirstOrDefault(c => c.Currency is not null)?.Currency;

        var totals = new TraceTotals(
            turns.Count, turns.Count(t => t.Failed), chats.Count, tools.Count,
            chats.Sum(c => c.Input), chats.Sum(c => c.Output),
            chats.Any(c => c.Cost is not null) ? chats.Sum(c => c.Cost ?? 0) : null, currency,
            turns.Count == 0 ? 0 : turns.Average(t => t.DurationMs));

        var models = chats.GroupBy(c => c.Model ?? "?")
            .Select(g =>
            {
                var durations = g.Select(c => c.DurationMs).Order().ToList();
                var ttfts = g.Where(c => c.TtftMs is not null).Select(c => c.TtftMs!.Value).ToList();
                return new ModelStatistics(
                    g.Key, g.Count(), g.Count(c => c.Failed), Percentile(durations, 0.5), Percentile(durations, 0.95),
                    ttfts.Count == 0 ? null : ttfts.Average(), g.Sum(c => c.Input), g.Sum(c => c.Output),
                    g.Any(c => c.Cost is not null) ? g.Sum(c => c.Cost ?? 0) : null,
                    g.Average(c => Number(c.Attributes, Telemetry.TagToolDefinitionsChars) ?? 0));
            })
            .OrderByDescending(m => m.Calls)
            .ToList();

        // Offered counts come from each model call's list of tools; tools offered but never called show up too.
        var offered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chat in chats)
        {
            if (chat.Attributes.GetValueOrDefault(Telemetry.TagToolsOffered) is { Length: > 0 } names)
            {
                foreach (var name in names.Split(','))
                    offered[name] = offered.GetValueOrDefault(name) + 1;
            }
        }
        var called = tools.GroupBy(t => t.ToolName!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var toolStats = offered.Keys.Union(called.Keys)
            .Select(name =>
            {
                var calls = called.GetValueOrDefault(name) ?? [];
                var sizes = calls.Select(c => Number(c.Attributes, Telemetry.TagResultChars)).Where(n => n is not null).Select(n => n!.Value).ToList();
                return new ToolStatistics(
                    name, calls.FirstOrDefault()?.ToolSource ?? (name is "load_skill" or "read_skill_file" ? "skill" : "—"),
                    offered.GetValueOrDefault(name), calls.Count, calls.Count(c => c.Failed),
                    calls.Count == 0 ? 0 : calls.Average(c => c.DurationMs), calls.Count == 0 ? 0 : calls.Max(c => c.DurationMs),
                    sizes.Count == 0 ? null : sizes.Average());
            })
            .OrderByDescending(t => t.Calls).ThenByDescending(t => t.Offered).ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var skills = tools.Where(t => t.Attributes.ContainsKey(Telemetry.TagSkill))
            .GroupBy(t => t.Attributes[Telemetry.TagSkill])
            .Select(g => new SkillStatistics(g.Key, g.Count(t => t.ToolName == "load_skill"), g.Count(t => t.ToolName == "read_skill_file"), g.Max(t => t.StartedAt)))
            .OrderByDescending(s => s.Loads)
            .ToList();

        var approvals = rows.Where(r => r.Operation == Telemetry.OpApproval)
            .GroupBy(r => r.Attributes.GetValueOrDefault(Telemetry.TagDecision) ?? "?")
            .Select(g => new ApprovalStatistics(g.Key, g.Count(), g.Average(r => r.DurationMs)))
            .OrderByDescending(a => a.Count)
            .ToList();

        return new TraceStatistics(totals, models, toolStats, skills, approvals);
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static double? Number(Dictionary<string, string> attributes, string key) =>
        double.TryParse(attributes.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private sealed record Row(
        string Operation, string? Model, double DurationMs, bool Failed, double? TtftMs, long Input, long Output,
        double? Cost, string? Currency, string? ToolName, string? ToolSource, Dictionary<string, string> Attributes, DateTimeOffset StartedAt);
}
