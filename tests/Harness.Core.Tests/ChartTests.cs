// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Harness.Core.Artifacts;
using Harness.Core.Files;

namespace Harness.Core.Tests;

public class ChartTests
{
    private const string Deck = """
        # Title
        ## Sub
        ---
        # Sales
        ```chart
        { "type": "column", "title": "Sales", "categories": ["2023", "2024", "2025"],
          "series": [{ "name": "North", "values": [12, 15, "18%"] }, { "name": "South", "values": [9, null, 14] }], "labels": true }
        ```
        ---
        # Mix
        - Point one
        - Point two
        ```chart
        { "type": "Donut", "categories": ["A", "B", "C"], "series": [{ "name": "Share", "values": [50, 30, 20] }, { "name": "ignored", "values": [1, 2, 3] }] }
        ```
        Notes: from the annual report
        """;

    [Fact]
    public void A_chart_block_is_read_leniently()
    {
        var deck = SlideDeck.Parse(Deck, isComplete: true);

        var column = Assert.IsType<ChartBlock>(Assert.Single(deck.Slides[1].Blocks));
        Assert.Equal("column", column.Kind);
        Assert.Equal("Sales", column.Title);
        Assert.Equal(["2023", "2024", "2025"], column.Categories);
        Assert.Equal([12d, 15d, 18d], column.Series[0].Values);    // "18%" is read as a number
        Assert.Equal([9d, null, 14d], column.Series[1].Values);    // a missing value stays missing
        Assert.True(column.Labels);

        var ring = deck.Slides[2].Blocks.OfType<ChartBlock>().Single();
        Assert.Equal("doughnut", ring.Kind);                        // "Donut" is a doughnut
        Assert.Single(ring.Series);                                  // a ring has one series
        Assert.Equal("from the annual report", deck.Slides[2].Notes);
    }

    [Fact]
    public void A_chart_that_cannot_be_read_says_so_instead_of_disappearing()
    {
        var deck = SlideDeck.Parse("# T\n---\n# S\n```chart\n{ \"type\": \"radar\", \"series\": [] }\n```\n- after", isComplete: true);

        var note = Assert.IsType<ParagraphBlock>(deck.Slides[1].Blocks[0]);
        Assert.StartsWith("[chart:", SlideDeck.TextOf(note.Runs));
        Assert.IsType<BulletBlock>(deck.Slides[1].Blocks[1]);        // and the slide goes on

        Assert.False(ChartBlock.TryParse("[1,2]", out _, out var error));
        Assert.Contains("object", error);
        Assert.False(ChartBlock.TryParse("{ \"type\": \"pie\", \"series\": [{\"name\": \"x\", \"values\": []}] }", out _, out _));
        Assert.True(ChartBlock.TryParse("{ \"type\": \"line\", \"data\": [[1,2,3]] }", out var loose, out _)); // another model's habits
        Assert.Equal(["1", "2", "3"], loose!.Categories);
    }

    [Fact]
    public void A_chart_still_being_written_is_not_shown_until_its_fence_closes()
    {
        var deck = SlideDeck.Parse("# T\n---\n# S\n```chart\n{ \"type\": \"pie\", \"categories\": [\"A\"],", isComplete: false);

        Assert.Empty(deck.Slides[1].Blocks);
        Assert.Single(SlideDeck.Parse("# T\n---\n# S\n```chart\n{ \"type\": \"pie\", \"categories\": [\"A\"], \"series\": [{\"name\":\"s\",\"values\":[1]}] }", isComplete: true).Slides[1].Blocks); // no closing fence at the end
    }

    [Fact]
    public void A_chart_survives_being_written_back_as_text()
    {
        var deck = SlideDeck.Parse(Deck, isComplete: true);

        var again = SlideDeck.Parse(deck.ToMarkdown(), isComplete: true);

        Assert.Equal(deck.ToMarkdown(), again.ToMarkdown());
        var chart = again.Slides[1].Blocks.OfType<ChartBlock>().Single();
        Assert.Equal([9d, null, 14d], chart.Series[1].Values);
        Assert.Equal(3, again.Slides[2].Blocks.Count); // two bullets and the chart
    }

    [Fact]
    public void A_chart_takes_the_whole_content_area_alone_and_the_right_part_beside_text()
    {
        var deck = SlideDeck.Parse(Deck, isComplete: true);
        var layout = SlideLayout.Default;

        var alone = layout.ChartFrame(deck.Slides[1])!;
        var beside = layout.ChartFrame(deck.Slides[2])!;

        Assert.Equal(layout.ContentWidth, alone.W, 1);
        Assert.True(beside.X > layout.ContentLeft + layout.ContentWidth * 0.4);
        Assert.True(beside.W < layout.ContentWidth * 0.6);
        Assert.Equal(layout.ContentWidth, layout.TextWidth(deck.Slides[1]), 1);
        Assert.True(layout.TextWidth(deck.Slides[2]) < layout.ContentWidth * 0.5);
        Assert.Null(layout.ChartFrame(deck.Slides[0]));
    }

    [Fact]
    public void The_page_gets_the_chart_with_the_theme_colours()
    {
        var json = JsonDocument.Parse(SlideDeck.Parse(Deck, true).ToJson()).RootElement;

        var chart = json.GetProperty("slides")[1].GetProperty("blocks")[0];
        Assert.Equal("chart", chart.GetProperty("t").GetString());
        Assert.Equal("column", chart.GetProperty("type").GetString());
        Assert.Equal(2, chart.GetProperty("series").GetArrayLength());
        Assert.True(json.GetProperty("slides")[1].TryGetProperty("chartFrame", out _));
        Assert.True(json.GetProperty("theme").GetProperty("palette").GetArrayLength() >= 4);
        Assert.False(string.IsNullOrEmpty(json.GetProperty("theme").GetProperty("ink").GetString()));
    }

    [Fact]
    public void The_pptx_has_real_charts_with_their_data_inside_and_validates()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "charts.pptx");

        PowerPointWriter.WritePresentation(path, SlideDeck.Parse(Deck, true), "Charts");

        using var document = PresentationDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(document));
        var slides = document.PresentationPart!.SlideParts.ToList();
        var columnChart = Assert.Single(slides[1].ChartParts);
        var xml = columnChart.ChartSpace!.OuterXml;
        Assert.Contains("c:barChart", xml);
        Assert.Contains("c:barDir val=\"col\"", xml);
        Assert.Contains("<c:v>North</c:v>", xml);
        Assert.Contains("c:min val=\"0\"", xml);                      // bars start at zero
        Assert.Contains("c:doughnutChart", Assert.Single(slides[2].ChartParts).ChartSpace!.OuterXml);

        // the workbook "Edit data" opens holds the same numbers
        var workbookPart = Assert.Single(columnChart.EmbeddedPackagePart is null ? [] : new[] { columnChart.EmbeddedPackagePart });
        using var stream = workbookPart.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        using var workbook = SpreadsheetDocument.Open(memory, false);
        var sheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet.InnerText;
        Assert.Contains("North", sheet);
        Assert.Contains("2025", sheet);
        Assert.Contains("14", sheet);
    }

    [Fact]
    public void A_company_theme_colours_the_chart_with_its_brand()
    {
        using var dir = new TempDir();
        var template = Path.Combine(dir.Path, "company.pptx");
        PowerPointWriter.WritePresentation(template, SlideDeck.Parse("# A\n---\n# B\n- x", true), "T");
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(template, "Co");

        Assert.Equal(theme.Content.Shapes.First(s => s.Kind is "line" or "rect").Color, theme.Palette[0]); // the colour of the template's rule

        var path = Path.Combine(dir.Path, "out.pptx");
        PowerPointWriter.WritePresentation(path, SlideDeck.Parse(Deck, true), "Out", theme, store.TemplatePathOf(theme));
        using var document = PresentationDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(document));
        Assert.Contains(theme.Palette[0], document.PresentationPart!.SlideParts.ToList()[1].ChartParts.Single().ChartSpace!.OuterXml);
    }
}
