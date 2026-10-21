// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Harness.Core.Artifacts;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Harness.Core.Files;

/// <summary>
/// Puts a real PowerPoint chart on a slide: a chart part that draws the chart and keeps its numbers, and an embedded workbook with the
/// same numbers, so "Edit data" in PowerPoint works and the chart can be restyled or changed to another type there.
/// </summary>
public static class ChartWriter
{
    private const string ChartNamespaces =
        "xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private const int MaxSeries = 24;

    /// <summary>Adds the chart (and its workbook) to the slide and returns the relationship id the slide's graphic frame refers to.</summary>
    /// <param name="palette">Colours for the series (for a pie, the slices).</param>
    /// <param name="ink">The colour of the chart's text, RRGGBB.</param>
    /// <param name="fonts">Latin and East Asian fonts for the text; null to inherit.</param>
    public static string AddChart(SlidePart slide, ChartBlock chart, IReadOnlyList<string> palette, string ink, (string? Latin, string? Ea) fonts)
    {
        chart = chart with { Series = [.. chart.Series.Take(MaxSeries)] };
        var part = slide.AddNewPart<ChartPart>();
        var workbook = part.AddEmbeddedPackagePart("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using (var data = new MemoryStream(Workbook(chart)))
            workbook.FeedData(data);
        part.ChartSpace = new C.ChartSpace(ChartXml(chart, palette, ink, fonts, part.GetIdOfPart(workbook)));
        return slide.GetIdOfPart(part);
    }

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Column letter of the series' column in the workbook: series 0 is B.</summary>
    private static string Column(int series) => ((char)('B' + series)).ToString();

    #region Workbook

    private static byte[] Workbook(ChartBlock chart)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();

            Row Row(int index, IEnumerable<Cell> cells) => new(cells) { RowIndex = (uint)index };
            Cell Text(string reference, string text) => new() { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) };
            Cell Number(string reference, double value) => new() { CellReference = reference, CellValue = new CellValue(Num(value)) };

            data.Append(Row(1, chart.Series.Select((s, i) => Text($"{Column(i)}1", s.Name)).Prepend(Text("A1", " "))));
            for (var r = 0; r < chart.Categories.Count; r++)
            {
                var cells = new List<Cell> { Text($"A{r + 2}", chart.Categories[r]) };
                for (var s = 0; s < chart.Series.Count; s++)
                {
                    if (chart.Series[s].Values[r] is { } value)
                        cells.Add(Number($"{Column(s)}{r + 2}", value));
                }
                data.Append(Row(r + 2, cells));
            }

            sheetPart.Worksheet = new Worksheet(data);
            workbook.Workbook.Append(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = "Sheet1" }));
        }

        return stream.ToArray();
    }

    #endregion

    #region Chart

    private static string ChartXml(ChartBlock chart, IReadOnlyList<string> palette, string ink, (string? Latin, string? Ea) fonts, string workbookRelationship)
    {
        var face = (fonts.Latin is null ? string.Empty : $"<a:latin typeface=\"{Esc(fonts.Latin)}\"/>") + (fonts.Ea is null ? string.Empty : $"<a:ea typeface=\"{Esc(fonts.Ea)}\"/>");
        var text = new StringBuilder();
        text.Append($"<c:chartSpace {ChartNamespaces}><c:date1904 val=\"0\"/><c:roundedCorners val=\"0\"/><c:chart>");
        if (chart.Title is { Length: > 0 } title)
        {
            text.Append("<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1600\" b=\"1\"/></a:pPr>")
                .Append($"<a:r><a:rPr lang=\"zh-TW\" sz=\"1600\" b=\"1\"><a:solidFill><a:srgbClr val=\"{ink}\"/></a:solidFill>{face}</a:rPr><a:t>{Esc(title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
        }
        text.Append($"<c:autoTitleDeleted val=\"{(chart.Title is { Length: > 0 } ? 0 : 1)}\"/><c:plotArea><c:layout/>");

        var colors = palette.Count > 0 ? palette : ["1F4E79"];
        string Color(int index) => colors[index % colors.Count];

        switch (chart.Kind)
        {
            case "pie":
            case "doughnut":
                text.Append(chart.Kind == "pie" ? "<c:pieChart><c:varyColors val=\"1\"/>" : "<c:doughnutChart><c:varyColors val=\"1\"/>");
                text.Append(Series(chart, 0, chart.Series[0], Color, circular: true));
                text.Append(Labels(chart, ink));
                text.Append(chart.Kind == "pie" ? "<c:firstSliceAng val=\"0\"/></c:pieChart>" : "<c:firstSliceAng val=\"0\"/><c:holeSize val=\"55\"/></c:doughnutChart>");
                break;

            case "line":
                text.Append("<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>");
                for (var i = 0; i < chart.Series.Count; i++)
                    text.Append(Series(chart, i, chart.Series[i], Color, circular: false));
                text.Append(Labels(chart, ink)).Append("<c:marker val=\"1\"/><c:axId val=\"111\"/><c:axId val=\"222\"/></c:lineChart>");
                text.Append(Axes(horizontalBars: false, crossBetween: "between", zeroBased: false));
                break;

            case "area":
                text.Append($"<c:areaChart><c:grouping val=\"{(chart.Stacked ? "stacked" : "standard")}\"/><c:varyColors val=\"0\"/>");
                for (var i = 0; i < chart.Series.Count; i++)
                    text.Append(Series(chart, i, chart.Series[i], Color, circular: false));
                text.Append(Labels(chart, ink)).Append("<c:axId val=\"111\"/><c:axId val=\"222\"/></c:areaChart>");
                text.Append(Axes(horizontalBars: false, crossBetween: "midCat", zeroBased: !HasNegative(chart)));
                break;

            default: // column, bar
                text.Append($"<c:barChart><c:barDir val=\"{(chart.Kind == "bar" ? "bar" : "col")}\"/><c:grouping val=\"{(chart.Stacked ? "stacked" : "clustered")}\"/><c:varyColors val=\"0\"/>");
                for (var i = 0; i < chart.Series.Count; i++)
                    text.Append(Series(chart, i, chart.Series[i], Color, circular: false));
                text.Append(Labels(chart, ink)).Append("<c:gapWidth val=\"80\"/>").Append(chart.Stacked ? "<c:overlap val=\"100\"/>" : string.Empty).Append("<c:axId val=\"111\"/><c:axId val=\"222\"/></c:barChart>");
                text.Append(Axes(horizontalBars: chart.Kind == "bar", crossBetween: "between", zeroBased: !HasNegative(chart)));
                break;
        }

        text.Append("</c:plotArea>");
        if (chart.Series.Count > 1 || chart.IsCircular)
            text.Append($"<c:legend><c:legendPos val=\"{(chart.IsCircular ? "r" : "b")}\"/><c:overlay val=\"0\"/></c:legend>");
        text.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>");
        text.Append($"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1200\"><a:solidFill><a:srgbClr val=\"{ink}\"/></a:solidFill>{face}</a:defRPr></a:pPr><a:endParaRPr lang=\"zh-TW\"/></a:p></c:txPr>");
        text.Append($"<c:externalData r:id=\"{workbookRelationship}\"><c:autoUpdate val=\"0\"/></c:externalData></c:chartSpace>");
        return text.ToString();
    }

    private static string Series(ChartBlock chart, int index, ChartSeries series, Func<int, string> color, bool circular)
    {
        var column = Column(index);
        var last = chart.Categories.Count + 1;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/>");
        text.Append($"<c:tx><c:strRef><c:f>Sheet1!${column}$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>{Esc(series.Name)}</c:v></c:pt></c:strCache></c:strRef></c:tx>");

        var fill = $"<a:solidFill><a:srgbClr val=\"{color(index)}\"/></a:solidFill>";
        if (chart.Kind == "line")
            text.Append($"<c:spPr><a:ln w=\"28575\" cap=\"rnd\">{fill}<a:round/></a:ln></c:spPr><c:marker><c:symbol val=\"circle\"/><c:size val=\"6\"/><c:spPr>{fill}<a:ln w=\"9525\">{fill}</a:ln></c:spPr></c:marker>");
        else if (!circular)
            text.Append($"<c:spPr>{fill}</c:spPr>{(chart.Kind is "column" or "bar" ? "<c:invertIfNegative val=\"0\"/>" : string.Empty)}");

        if (circular)
        {
            for (var i = 0; i < chart.Categories.Count; i++)
                text.Append(CultureInfo.InvariantCulture, $"<c:dPt><c:idx val=\"{i}\"/><c:bubble3D val=\"0\"/><c:spPr><a:solidFill><a:srgbClr val=\"{color(i)}\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln></c:spPr></c:dPt>");
        }

        text.Append(CultureInfo.InvariantCulture, $"<c:cat><c:strRef><c:f>Sheet1!$A$2:$A${last}</c:f><c:strCache><c:ptCount val=\"{chart.Categories.Count}\"/>");
        for (var i = 0; i < chart.Categories.Count; i++)
            text.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{Esc(chart.Categories[i])}</c:v></c:pt>");
        text.Append("</c:strCache></c:strRef></c:cat>");

        text.Append(CultureInfo.InvariantCulture, $"<c:val><c:numRef><c:f>Sheet1!${column}$2:${column}${last}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{series.Values.Count}\"/>");
        for (var i = 0; i < series.Values.Count; i++)
        {
            if (series.Values[i] is { } value)
                text.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{Num(value)}</c:v></c:pt>");
        }
        text.Append("</c:numCache></c:numRef></c:val>");
        if (chart.Kind == "line")
            text.Append("<c:smooth val=\"0\"/>");
        text.Append("</c:ser>");
        return text.ToString();
    }

    private static string Labels(ChartBlock chart, string ink) =>
        "<c:dLbls><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>" +
        (chart.Kind == "doughnut"
            ? "<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr b=\"1\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"zh-TW\"/></a:p></c:txPr>"
            : string.Empty) +
        (chart.Kind == "pie" ? "<c:dLblPos val=\"outEnd\"/>" : string.Empty) +
        $"<c:showLegendKey val=\"0\"/><c:showVal val=\"{(chart.Labels && !chart.IsCircular ? 1 : 0)}\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/>" +
        $"<c:showPercent val=\"{(chart.Labels && chart.IsCircular ? 1 : 0)}\"/><c:showBubbleSize val=\"0\"/></c:dLbls>";

    private static bool HasNegative(ChartBlock chart) => chart.Series.Any(s => s.Values.Any(v => v < 0));

    private static string Axes(bool horizontalBars, string crossBetween, bool zeroBased) =>
        $"<c:catAx><c:axId val=\"111\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{(horizontalBars ? "l" : "b")}\"/>" +
        "<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>" +
        $"<c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"A6A6A6\"/></a:solidFill></a:ln></c:spPr><c:crossAx val=\"222\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>" +
        $"<c:valAx><c:axId val=\"222\"/><c:scaling><c:orientation val=\"minMax\"/>{(zeroBased ? "<c:min val=\"0\"/>" : string.Empty)}</c:scaling><c:delete val=\"0\"/><c:axPos val=\"{(horizontalBars ? "b" : "l")}\"/>" +
        "<c:majorGridlines><c:spPr><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"D9D9D9\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>" +
        "<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>" +
        $"<c:spPr><a:ln><a:noFill/></a:ln></c:spPr><c:crossAx val=\"111\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"{crossBetween}\"/></c:valAx>";

    #endregion
}
