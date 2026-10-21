// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Core.Artifacts;

/// <summary>One line, set of bars or ring of a chart; a value is null where there is none.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values);

/// <summary>
/// A chart on a slide, written by the model in the deck as a <c>```chart</c> block holding JSON (see <see cref="TryParse"/>): its type, an
/// optional title, the categories and one or more series of numbers. Only the content is open: the type is one of
/// <see cref="Kinds"/>. The preview draws it, and the .pptx gets a real PowerPoint chart with its data table inside, so the person can
/// open "Edit data" in PowerPoint.
/// </summary>
/// <param name="Kind">column (vertical bars), bar (horizontal bars), line, area, pie or doughnut.</param>
/// <param name="Stacked">For column, bar and area: the series are stacked.</param>
/// <param name="Labels">Show the values on the chart.</param>
public sealed record ChartBlock(
    [property: JsonPropertyName("type")] string Kind, string? Title, IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series, bool Stacked, bool Labels)
    : SlideBlock
{
    public static readonly IReadOnlyList<string> Kinds = ["column", "bar", "line", "area", "pie", "doughnut"];

    /// <summary>Pie and doughnut have one series and no axes.</summary>
    [JsonIgnore]
    public bool IsCircular => Kind is "pie" or "doughnut";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the JSON the model wrote; false (with a reason) when it can't be a chart. Lenient about what a model tends to write.</summary>
    public static bool TryParse(string json, out ChartBlock? chart, out string error)
    {
        chart = null;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "a chart is a JSON object";
                return false;
            }

            var kind = NormalizeKind(Text(root, "type") ?? Text(root, "kind") ?? Text(root, "chartType"));
            if (kind is null)
            {
                error = $"type must be one of {string.Join(", ", Kinds)}";
                return false;
            }

            var categories = new List<string>();
            if (Find(root, "categories", "labels", "x") is { ValueKind: JsonValueKind.Array } cats)
                categories.AddRange(cats.EnumerateArray().Select(Display));

            var series = new List<ChartSeries>();
            if (Find(root, "series", "datasets", "data") is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && Find(item, "values", "data", "y") is { ValueKind: JsonValueKind.Array } values)
                        series.Add(new ChartSeries(Text(item, "name") ?? Text(item, "label") ?? $"Series {series.Count + 1}", [.. values.EnumerateArray().Select(Number)]));
                    else if (item.ValueKind == JsonValueKind.Array)
                        series.Add(new ChartSeries($"Series {series.Count + 1}", [.. item.EnumerateArray().Select(Number)]));
                }
            }

            if (series.Count == 0 || series.All(s => s.Values.Count == 0))
            {
                error = "a chart needs at least one series with values";
                return false;
            }

            // A chart is as long as its longest series; categories and series are filled up to it.
            var length = Math.Max(series.Max(s => s.Values.Count), categories.Count);
            while (categories.Count < length)
                categories.Add((categories.Count + 1).ToString(CultureInfo.InvariantCulture));
            series = [.. series.Select(s => s with { Values = [.. s.Values, .. Enumerable.Repeat<double?>(null, length - s.Values.Count)] })];
            if (kind is "pie" or "doughnut")
                series = [series[0]]; // one ring

            chart = new ChartBlock(
                kind, Text(root, "title") is { Length: > 0 } title ? title : null, categories, series,
                Flag(root, "stacked"), Flag(root, "labels") || Flag(root, "dataLabels") || Flag(root, "showValues"));
            return true;
        }
        catch (JsonException ex)
        {
            error = "the JSON can't be read: " + ex.Message;
            return false;
        }
    }

    /// <summary>The chart as the JSON the model writes (what is stored in the deck's text).</summary>
    public string ToDeckJson() => JsonSerializer.Serialize(
        new
        {
            type = Kind, title = Title, categories = Categories, series = Series.Select(s => new { name = s.Name, values = s.Values }),
            stacked = Stacked ? true : (bool?)null, labels = Labels ? true : (bool?)null,
        },
        s_json);

    private static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal) switch
    {
        "column" or "vertical-bar" or "columns" or "bar-vertical" or "column-chart" => "column",
        "bar" or "horizontal-bar" or "bar-horizontal" or "hbar" or "bar-chart" => "bar",
        "line" or "line-chart" => "line",
        "area" or "area-chart" => "area",
        "pie" or "pie-chart" => "pie",
        "doughnut" or "donut" or "ring" or "doughnut-chart" => "doughnut",
        _ => null,
    };

    private static JsonElement? Find(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement parent, string name) =>
        Find(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Flag(JsonElement parent, string name) =>
        Find(parent, name) is { } value && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() is "true" or "yes"));

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();

    /// <summary>A number, from a number or from a text such as "12%" or "1,200"; null for anything else.</summary>
    private static double? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString()!.Replace(",", string.Empty, StringComparison.Ordinal).TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
