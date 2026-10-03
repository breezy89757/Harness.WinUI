// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Core.Config;
using Harness.Core.Files;
using Harness.Core.Tools;

namespace Harness.Core.Observability;

/// <summary>
/// Listens to <see cref="Telemetry.Source"/> while recording is on and writes each finished span to a
/// <see cref="TraceStore"/> on a background task. While it is off nothing listens, so the instrumentation
/// costs nothing (Microsoft.Extensions.AI skips building span data when a source has no listener).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TraceRecorder : IDisposable
{
    private readonly Channel<Activity> _finished = Channel.CreateUnbounded<Activity>(new UnboundedChannelOptions { SingleReader = true });
    private ActivityListener? _listener;

    public TraceRecorder(TraceStore store)
    {
        Store = store;
        _ = Task.Run(WriteAsync);
    }

    public TraceStore Store { get; }

    public bool IsRecording => _listener is not null;

    /// <summary>Raised on a background thread after a turn's root span is saved (the turn is complete); the argument is its trace id.</summary>
    public event EventHandler<string>? TurnRecorded;

    /// <summary>Starts or stops recording, and removes traces older than <paramref name="retentionDays"/>.</summary>
    public void Configure(bool record, bool captureContent, int retentionDays)
    {
        Telemetry.CaptureContent = captureContent;
        if (record && _listener is null)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == Telemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _finished.Writer.TryWrite(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }
        else if (!record && _listener is not null)
        {
            _listener.Dispose();
            _listener = null;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Store.Purge(Math.Max(1, retentionDays));
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                Debug.WriteLine($"Trace purge failed: {ex.Message}");
            }
        });
    }

    private async Task WriteAsync()
    {
        await foreach (var activity in _finished.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Store.Insert(ToRecord(activity));
                if (activity.OperationName == Telemetry.OpTurn)
                    TurnRecorded?.Invoke(this, activity.TraceId.ToHexString());
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
            {
                Debug.WriteLine($"Couldn't record span {activity.DisplayName}: {ex.Message}");
            }
        }
    }

    private static SpanRecord ToRecord(Activity activity)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in activity.TagObjects)
        {
            if (value is null)
                continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (Telemetry.ContentTags.Contains(key))
            {
                if (Telemetry.CaptureContent)
                    content[key] = Telemetry.Redact(text);
            }
            else
                attributes[key] = text;
        }

        AddDerived(activity, attributes);

        // MEAI names its spans "chat {model}" / "execute_tool {name}"; the operation is in gen_ai.operation.name.
        var operation = attributes.GetValueOrDefault("gen_ai.operation.name") ?? activity.OperationName;
        var model = attributes.GetValueOrDefault(Telemetry.TagRequestModel) ?? attributes.GetValueOrDefault(Telemetry.TagResponseModel);
        var input = Number(attributes, Telemetry.TagInputTokens);
        var output = Number(attributes, Telemetry.TagOutputTokens);
        var cached = Number(attributes, Telemetry.TagCachedTokens);
        var ttft = double.TryParse(attributes.GetValueOrDefault(Telemetry.TagTimeToFirstChunk), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds * 1000 : (double?)null;

        double? cost = null;
        string? currency = null;
        if (operation == Telemetry.OpChat && input is not null && output is not null)
        {
            var preferences = AppPreferences.Load();
            if (preferences.PriceFor(model) is { } price)
            {
                cost = (double)price.Cost(input.Value, cached ?? 0, output.Value);
                currency = preferences.Currency;
            }
        }

        var toolName = attributes.GetValueOrDefault(Telemetry.TagToolName);
        var conversation = attributes.GetValueOrDefault(Telemetry.TagConversation);
        return new SpanRecord(
            activity.SpanId.ToHexString(), activity.TraceId.ToHexString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            operation, activity.DisplayName, activity.StartTimeUtc, activity.Duration.TotalMilliseconds,
            activity.Status == ActivityStatusCode.Error, activity.StatusDescription,
            conversation, model, input, output, cached, Number(attributes, Telemetry.TagReasoningTokens), ttft, cost, currency,
            toolName, toolName is null ? null : ToolSource(toolName, attributes.GetValueOrDefault(Telemetry.TagToolSource)),
            attributes, content);
    }

    /// <summary>
    /// Facts the statistics need that would otherwise only be in the content (which may not be kept):
    /// which tools a model call was offered and how large their definitions were, how large a tool's
    /// result was, and which skill a skill tool used.
    /// </summary>
    private static void AddDerived(Activity activity, Dictionary<string, string> attributes)
    {
        if (activity.GetTagItem(Telemetry.TagToolDefinitions) is string definitions)
        {
            attributes[Telemetry.TagToolDefinitionsChars] = definitions.Length.ToString(CultureInfo.InvariantCulture);
            try
            {
                using var document = JsonDocument.Parse(definitions);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    attributes[Telemetry.TagToolsOffered] = string.Join(",", document.RootElement.EnumerateArray()
                        .Select(t => t.TryGetProperty("name", out var name) ? name.GetString() : null)
                        .Where(n => n is not null));
                }
            }
            catch (JsonException)
            {
            }
        }

        if (activity.GetTagItem(Telemetry.TagToolResult) is string result)
            attributes[Telemetry.TagResultChars] = result.Length.ToString(CultureInfo.InvariantCulture);

        if (activity.GetTagItem(Telemetry.TagToolName) is "load_skill" or "read_skill_file" &&
            activity.GetTagItem(Telemetry.TagToolArguments) is string arguments)
        {
            try
            {
                using var document = JsonDocument.Parse(arguments);
                if (document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    attributes[Telemetry.TagSkill] = name.GetString()!;
            }
            catch (JsonException)
            {
            }
        }
    }

    /// <summary>"built-in", "skill" or "mcp:{server}".</summary>
    private static string ToolSource(string toolName, string? server) => server switch
    {
        _ when toolName is "load_skill" or "read_skill_file" => "skill",
        null or FileTools.ServerName or McpServerManager.BuiltInServerName => "built-in",
        _ => "mcp:" + server,
    };

    private static long? Number(Dictionary<string, string> attributes, string key) =>
        long.TryParse(attributes.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public void Dispose()
    {
        _listener?.Dispose();
        _finished.Writer.TryComplete();
    }
}
