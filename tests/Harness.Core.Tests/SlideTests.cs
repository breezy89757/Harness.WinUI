// Harness.WinUI — Licensed under the MIT License.

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Harness.Core.Artifacts;
using Harness.Core.Files;

namespace Harness.Core.Tests;

public class SlideTests
{
    private const string Sample = """
        # MCP 使用研究
        ## 用法整理
        王小明
        ---
        # 為什麼
        - 一個標準協定，連到**外部工具**與*資料*
        - 好處
          - 不必重寫
          - 可重複使用
        1. 第一步
        2. 第二步
        Notes: 先講動機。
        第二行備註
        ---
        # 比較
        | 用法 | 做什麼 |
        |---|---|
        | Tools | 模型呼叫 |
        | Resources | 應用決定 |
        ---
        # 第二部分
        ## 落地
        """;

    [Fact]
    public void A_deck_is_split_into_title_content_and_section_slides()
    {
        var deck = SlideDeck.Parse(Sample, isComplete: true);

        Assert.Equal([SlideKind.Title, SlideKind.Content, SlideKind.Content, SlideKind.Section], deck.Slides.Select(s => s.Kind));
        Assert.Equal("MCP 使用研究", deck.Slides[0].Title);
        Assert.Equal("用法整理", deck.Slides[0].Subtitle);
        Assert.Equal("王小明", SlideDeck.TextOf(((ParagraphBlock)deck.Slides[0].Blocks[0]).Runs));
        Assert.Equal("落地", deck.Slides[3].Subtitle);
        Assert.All(deck.Slides, s => Assert.True(s.IsComplete));
    }

    [Fact]
    public void Lists_nest_by_indent_and_marks_become_runs()
    {
        var slide = SlideDeck.Parse(Sample, isComplete: true).Slides[1];
        var items = slide.Blocks.OfType<BulletBlock>().ToList();

        Assert.Equal([0, 0, 1, 1, 0, 0], items.Select(i => i.Level));
        Assert.Equal([false, false, false, false, true, true], items.Select(i => i.Numbered));
        Assert.Contains(items[0].Runs, r => r.Bold && r.Text == "外部工具");
        Assert.Contains(items[0].Runs, r => r.Italic && r.Text == "資料"); // next to Chinese characters too
        Assert.Equal("先講動機。\n第二行備註", slide.Notes);
    }

    [Fact]
    public void A_table_keeps_its_header_and_its_separator_is_not_a_slide_break()
    {
        var table = SlideDeck.Parse(Sample, isComplete: true).Slides[2].Blocks.OfType<TableBlock>().Single();

        Assert.True(table.HasHeader);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Resources", SlideDeck.TextOf(table.Rows[2][0]));
    }

    [Fact]
    public void While_streaming_only_the_last_slide_is_not_complete()
    {
        var deck = SlideDeck.Parse("# A\n---\n# B\n- one\n- tw", isComplete: false);

        Assert.Equal([true, false], deck.Slides.Select(s => s.IsComplete));
        Assert.Equal("tw", SlideDeck.TextOf(((BulletBlock)deck.Slides[1].Blocks[1]).Runs));
    }

    [Fact]
    public void A_leading_or_doubled_separator_does_not_make_empty_slides()
    {
        var deck = SlideDeck.Parse("---\n# A\n---\n---\n# B\n", isComplete: true);

        Assert.Equal(["A", "B"], deck.Slides.Select(s => s.Title));
    }

    [Fact]
    public void The_artifact_parser_knows_the_slides_type()
    {
        var segments = ArtifactParser.Parse("<artifact id=\"d\" type=\"slides\" title=\"Deck\">\n# A\n---\n# B\n</artifact>", streamIsComplete: true);

        var artifact = Assert.IsType<ArtifactSegment>(Assert.Single(segments));
        Assert.Equal(ArtifactType.Slides, artifact.Type);
    }

    [Fact]
    public void The_preview_gets_the_same_font_sizes_the_pptx_uses()
    {
        var many = SlideDeck.Parse("# T\n---\n# Lots\n" + string.Concat(Enumerable.Range(1, 14).Select(i => $"- 第 {i} 項，說明比較長一點的文字內容，會佔用不只一行的空間\n")), isComplete: true).Slides[1];
        var few = SlideDeck.Parse("# T\n---\n# Few\n- one\n- two", isComplete: true).Slides[1];

        var layout = SlideLayout.Default;
        Assert.True(layout.BodyPoints(many) < layout.BodyPoints(few));
        Assert.True(layout.TextHeight(many, layout.BodyPoints(many)) <= layout.ContentHeight);
        var json = System.Text.Json.JsonDocument.Parse(SlideDeck.Parse(Sample, true).ToJson()).RootElement.GetProperty("slides");
        Assert.Equal("title", json[0].GetProperty("kind").GetString()); // the first slide must say it is the title slide
        Assert.Equal("section", json[3].GetProperty("kind").GetString());
        Assert.True(json[1].GetProperty("bodyPt").GetDouble() > 0);
    }

    [Fact]
    public void The_pptx_is_a_valid_package_with_real_placeholders_tables_and_notes()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deck.pptx");

        PowerPointWriter.WritePresentation(path, SlideDeck.Parse(Sample, isComplete: true), "MCP");

        using var document = PresentationDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(document));
        var presentation = document.PresentationPart!;
        var slides = presentation.SlideParts.ToList();
        Assert.Equal(4, slides.Count);
        Assert.Equal(4, presentation.Presentation!.SlideIdList!.Count()); // the slides are in the presentation's list, not only in the package
        Assert.Equal("MCP", document.PackageProperties.Title);
        Assert.Contains("為什麼", slides[1].Slide!.InnerText);
        Assert.Contains("Resources", slides[2].Slide!.InnerText);
        Assert.Contains("a:tbl", slides[2].Slide!.OuterXml);              // a real table
        Assert.Contains("type=\"title\"", slides[1].Slide!.OuterXml);     // the title is the layout's placeholder
        Assert.Contains("先講動機", slides[1].NotesSlidePart!.NotesSlide!.InnerText);
        Assert.Null(slides[2].NotesSlidePart);
    }

    private static System.Text.Json.JsonElement Op(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_deck_written_back_as_text_reads_the_same()
    {
        var deck = SlideDeck.Parse(Sample, isComplete: true);

        var again = SlideDeck.Parse(deck.ToMarkdown(), isComplete: true);

        Assert.Equal(deck.Slides.Count, again.Slides.Count);
        for (var i = 0; i < deck.Slides.Count; i++)
        {
            Assert.Equal(deck.Slides[i].Kind, again.Slides[i].Kind);
            Assert.Equal(deck.Slides[i].Title, again.Slides[i].Title);
            Assert.Equal(deck.Slides[i].Subtitle, again.Slides[i].Subtitle);
            Assert.Equal(deck.Slides[i].Notes, again.Slides[i].Notes);
            Assert.Equal(deck.Slides[i].Blocks.Count, again.Slides[i].Blocks.Count);
        }
        Assert.Equal(deck.ToMarkdown(), again.ToMarkdown()); // stable
        Assert.Contains(((BulletBlock)again.Slides[1].Blocks[0]).Runs, r => r.Bold && r.Text == "外部工具");
    }

    [Fact]
    public void Editing_text_changes_the_slide_and_keeps_the_marks()
    {
        var deck = SlideDeck.Parse(Sample, isComplete: true);

        var edited = SlideEdit.Apply(deck, Op("""{"op":"text","slide":2,"target":"block","block":0,"md":"新的**重點**"}"""));
        edited = SlideEdit.Apply(edited, Op("""{"op":"text","slide":3,"target":"cell","block":0,"row":1,"col":1,"md":"改過了"}"""));
        edited = SlideEdit.Apply(edited, Op("""{"op":"text","slide":1,"target":"title","md":"新標題"}"""));
        edited = SlideEdit.Apply(edited, Op("""{"op":"text","slide":2,"target":"notes","md":"新備註"}"""));

        Assert.Equal("新標題", edited.Slides[0].Title);
        Assert.Contains(((BulletBlock)edited.Slides[1].Blocks[0]).Runs, r => r.Bold && r.Text == "重點");
        Assert.Equal("新備註", edited.Slides[1].Notes);
        Assert.Equal("改過了", SlideDeck.TextOf(((TableBlock)edited.Slides[2].Blocks[0]).Rows[1][1]));
        Assert.Equal("MCP 使用研究", deck.Slides[0].Title); // the deck that was edited is not changed
    }

    [Fact]
    public void Lines_and_slides_can_be_added_removed_copied_and_moved()
    {
        var deck = SlideDeck.Parse(Sample, isComplete: true);

        var more = SlideEdit.Apply(deck, Op("""{"op":"insertAfter","slide":2,"block":1}"""));
        Assert.Equal(deck.Slides[1].Blocks.Count + 1, more.Slides[1].Blocks.Count);
        var added = (BulletBlock)more.Slides[1].Blocks[2];
        Assert.Equal(((BulletBlock)deck.Slides[1].Blocks[1]).Level, added.Level);
        Assert.Equal(string.Empty, SlideDeck.TextOf(added.Runs));
        Assert.Equal(more.Slides[1].Blocks.Count, SlideDeck.Parse(more.ToMarkdown(), true).Slides[1].Blocks.Count); // an empty line survives being stored

        var fewer = SlideEdit.Apply(more, Op("""{"op":"removeBlock","slide":2,"block":2}"""));
        Assert.Equal(deck.Slides[1].Blocks.Count, fewer.Slides[1].Blocks.Count);

        var withNew = SlideEdit.Apply(deck, Op("""{"op":"addSlide","slide":1}"""), "New");
        Assert.Equal(["MCP 使用研究", "New", "為什麼", "比較", "第二部分"], withNew.Slides.Select(s => s.Title));
        Assert.Equal(SlideKind.Content, withNew.Slides[1].Kind);

        var copy = SlideEdit.Apply(deck, Op("""{"op":"duplicateSlide","slide":3}"""));
        Assert.Equal(["MCP 使用研究", "為什麼", "比較", "比較", "第二部分"], copy.Slides.Select(s => s.Title));
        Assert.Equal([1, 2, 3, 4, 5], copy.Slides.Select(s => s.Number));

        var moved = SlideEdit.Apply(deck, Op("""{"op":"moveSlide","slide":3,"delta":-1}"""));
        Assert.Equal(["MCP 使用研究", "比較", "為什麼", "第二部分"], moved.Slides.Select(s => s.Title));

        var fewerSlides = SlideEdit.Apply(deck, Op("""{"op":"deleteSlide","slide":2}"""));
        Assert.Equal(3, fewerSlides.Slides.Count);
        Assert.Equal(SlideKind.Title, SlideEdit.Apply(deck, Op("""{"op":"moveSlide","slide":2,"delta":-1}""")).Slides[0].Kind); // the first slide is always the title slide
    }

    [Fact]
    public void A_message_that_makes_no_sense_leaves_the_deck_alone()
    {
        var deck = SlideDeck.Parse(Sample, isComplete: true);

        Assert.Same(deck, SlideEdit.Apply(deck, Op("""{"op":"text","slide":99,"target":"title","md":"x"}""")));
        Assert.Same(deck, SlideEdit.Apply(deck, Op("""{"op":"nothing","slide":1}""")));
        Assert.Same(deck, SlideEdit.Apply(deck, Op("""{"op":"removeBlock","slide":2,"block":99}""")));
        Assert.Same(deck, SlideEdit.Apply(deck, Op("""{"op":"text","slide":3,"target":"cell","block":0,"row":9,"col":0,"md":"x"}""")));
        Assert.Single(SlideEdit.Apply(SlideDeck.Parse("# Only", true), Op("""{"op":"deleteSlide","slide":1}""")).Slides); // the last slide stays
    }

    [Fact]
    public void A_quote_line_and_an_escaped_pipe_are_read_as_plain_text()
    {
        var slide = SlideDeck.Parse("# T\n---\n# S\n> MCP 就像 USB-C\n| a | b\\|c |\n|---|---|\n| 1 | 2 |", true).Slides[1];

        Assert.Equal("MCP 就像 USB-C", SlideDeck.TextOf(((ParagraphBlock)slide.Blocks[0]).Runs));
        var table = (TableBlock)slide.Blocks[1];
        Assert.Equal("b|c", SlideDeck.TextOf(table.Rows[0][1]));
        Assert.Contains("b\\|c", SlideDeck.From([slide]).ToMarkdown());
    }}
