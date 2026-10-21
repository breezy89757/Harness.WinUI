// Harness.WinUI — Licensed under the MIT License.

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Harness.Core.Artifacts;
using Harness.Core.Files;
using P = DocumentFormat.OpenXml.Presentation;

namespace Harness.Core.Tests;

public class ThemeTests
{
    private const string Sample = "# Deck\n## Sub\nPresenter\n2026\n---\n# One\n- a\n- b\nNotes: n1\n---\n# Table\n| h1 | h2 |\n|---|---|\n| x | y |\n---\n# Part\n## Two";

    // A stand-in for a company template: made by the program itself, so the tests hold no company's brand. Optionally with a logo
    // (a 1x1 picture) on the slide master.
    private static string MakeTemplate(TempDir dir, bool withLogo)
    {
        var path = Path.Combine(dir.Path, "company.pptx");
        PowerPointWriter.WritePresentation(path, SlideDeck.Parse(Sample, true), "Template");
        if (withLogo)
        {
            using var document = PresentationDocument.Open(path, true);
            var master = document.PresentationPart!.SlideMasterParts.First();
            var image = master.AddImagePart(ImagePartType.Png);
            using (var png = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")))
                image.FeedData(png);
            var relationship = master.GetIdOfPart(image);
            var tree = master.SlideMaster!.CommonSlideData!.ShapeTree!;
            tree.Append(new P.Picture(
                $"<p:pic xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<p:nvPicPr><p:cNvPr id=\"90\" name=\"Logo\"/><p:cNvPicPr/><p:nvPr userDrawn=\"1\"/></p:nvPicPr>" +
                $"<p:blipFill><a:blip r:embed=\"{relationship}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                "<p:spPr><a:xfrm><a:off x=\"6350000\" y=\"6200000\"/><a:ext cx=\"2540000\" cy=\"381000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>"));
        }
        return path;
    }

    [Fact]
    public void A_theme_is_read_from_a_template_with_its_layouts_and_logo()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));

        var theme = store.Import(MakeTemplate(dir, withLogo: true), "Test Co");

        Assert.False(theme.IsBuiltIn);
        Assert.Equal(960, theme.Width, 1);
        Assert.Equal("Title Slide", theme.Title.Name);
        Assert.Equal("Title and Content", theme.Content.Name);
        Assert.Equal("Section Header", theme.Section!.Name);
        Assert.NotNull(theme.Content.Role("body"));
        Assert.NotNull(theme.Title.Role("sub1"));
        Assert.Null(theme.Title.Role("body")); // a title layout has lines under the title, not a body
        Assert.Null(theme.Content.Role("sub1"));
        Assert.Equal(32, theme.Content.Role("title")!.Look.Size);

        // what the master draws on every slide: the logo, and the rule (a filled rectangle here)
        var logo = Assert.Single(theme.Content.Shapes, s => s.Kind == "image");
        Assert.Equal(200, logo.Frame.W, 1);
        Assert.True(File.Exists(Path.Combine(store.FolderOf(theme.Id), logo.Image!)));
        Assert.Contains(theme.Content.Shapes, s => s.Kind == "rect");
        Assert.NotNull(store.TemplatePathOf(theme));
    }

    [Fact]
    public void The_store_lists_resolves_and_deletes_themes_and_falls_back_to_the_built_in_one()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        Assert.Equal([BrandTheme.DefaultId], store.List().Select(t => t.Id));

        var theme = store.Import(MakeTemplate(dir, withLogo: false), "Test Co");

        Assert.Equal(["default", theme.Id], store.List().Select(t => t.Id));
        Assert.Equal("Test Co", store.Resolve(theme.Id).Name);
        Assert.True(store.Resolve("nothing").IsBuiltIn);
        Assert.True(store.Resolve(null).IsBuiltIn);

        store.Delete(theme.Id);
        Assert.Equal(["default"], store.List().Select(t => t.Id));
        Assert.True(store.Resolve(theme.Id).IsBuiltIn); // a deleted theme chosen earlier just means the built-in one
    }

    [Fact]
    public void Pictures_are_published_next_to_the_preview_page()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: true), "Test Co");
        var previews = Path.Combine(dir.Path, "artifacts");

        store.PublishAssets(theme, previews);
        var json = SlideDeck.Parse(Sample, true).ToJson(theme);

        var image = theme.Content.Shapes.First(s => s.Kind == "image").Image!;
        Assert.True(File.Exists(Path.Combine(previews, "_themes", theme.Id, image)));
        Assert.DoesNotContain(".pptx", Directory.EnumerateFiles(Path.Combine(previews, "_themes", theme.Id)).Select(Path.GetExtension));
        Assert.Contains($"_themes/{theme.Id}/", json);
        Assert.Contains("\"theme\"", json);
    }

    [Fact]
    public void A_deck_exported_with_a_theme_is_made_on_the_template_and_uses_its_layouts()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: true), "Test Co");
        var path = Path.Combine(dir.Path, "out.pptx");

        PowerPointWriter.WritePresentation(path, SlideDeck.Parse(Sample, true), "Out", theme, store.TemplatePathOf(theme));

        using var document = PresentationDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(document));
        var slides = document.PresentationPart!.SlideParts.ToList();
        Assert.Equal(4, slides.Count);
        string LayoutOf(SlidePart slide) => slide.SlideLayoutPart!.SlideLayout!.CommonSlideData!.Name!.Value!;
        Assert.Equal(["Title Slide", "Title and Content", "Title and Content", "Section Header"], slides.Select(LayoutOf));
        Assert.Contains("Deck", slides[0].Slide!.InnerText);
        Assert.Contains("Presenter", slides[0].Slide!.InnerText);
        Assert.Contains("a:tbl", slides[2].Slide!.OuterXml);
        Assert.Contains("n1", slides[1].NotesSlidePart!.NotesSlide!.InnerText);
        // the logo is still in the master the slides use
        Assert.Contains("Logo", slides[1].SlideLayoutPart!.SlideMasterPart!.SlideMaster!.OuterXml);
        Assert.Equal(3, document.PresentationPart.SlideMasterParts.First().SlideLayoutParts.Count());
    }

    [Fact]
    public void A_company_theme_without_its_template_file_cannot_export()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: false), "Test Co");

        Assert.Throws<FileNotFoundException>(() =>
            PowerPointWriter.WritePresentation(Path.Combine(dir.Path, "x.pptx"), SlideDeck.Parse(Sample, true), "x", theme, templatePath: null));
    }

    [Fact]
    public void A_file_that_is_not_a_presentation_is_refused()
    {
        using var dir = new TempDir();
        var bad = dir.Write("notes.pptx", "this is not a zip file");
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));

        Assert.Throws<InvalidDataException>(() => store.Import(bad, "Bad"));
        Assert.Equal([BrandTheme.DefaultId], store.List().Select(t => t.Id)); // nothing half-made is left behind
    }

    [Fact]
    public void A_title_too_long_for_its_frame_is_set_smaller()
    {
        var layout = SlideLayout.Default;
        var deck = SlideDeck.Parse("# Short\n---\n# A very long title that goes on and on and could not possibly fit in one line of the frame\n- x\n---\n# " + new string('長', 80) + "\n- x", true);

        Assert.Equal(44, layout.TitlePoints(deck.Slides[0]), 0); // a short title keeps the layout's size
        Assert.True(layout.TitlePoints(deck.Slides[1]) < 32, "long latin title: " + layout.TitlePoints(deck.Slides[1]));
        Assert.True(layout.TitlePoints(deck.Slides[2]) >= 12, "long cjk title: " + layout.TitlePoints(deck.Slides[2]));
    }

    [Fact]
    public void The_built_in_theme_keeps_the_sizes_it_always_had()
    {
        var slide = SlideDeck.Parse("# T\n---\n# S\n- one\n- two", true).Slides[1];

        Assert.Equal(28, new SlideLayout(BrandTheme.BuiltIn).BodyPoints(slide));
        Assert.True(new SlideLayout(BrandTheme.BuiltIn with { BodyMax = 20 }).BodyPoints(slide) <= 20);
    }

    // A model that answers every request with the same text, and remembers what it was asked.
    private sealed class FixedClient(string answer) : Microsoft.Extensions.AI.IChatClient
    {
        public List<string> Asked { get; } = [];

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Asked.AddRange(messages.Select(m => m.Text));
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, answer)));
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void What_the_samples_put_in_a_place_is_kept_as_its_hint()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));

        var theme = store.Import(MakeTemplate(dir, withLogo: false), "Test Co");

        // the template's own cover says "Sub" under the title, and its title place says "Deck"
        Assert.Equal("Sub", theme.Title.Role("sub1")!.Hint);
        Assert.Equal("Deck", theme.Title.Role("title")!.Hint);
    }

    [Fact]
    public async Task The_analysis_gives_the_model_the_facts_and_keeps_its_guide_without_a_code_fence()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: true), "Test Co");
        var client = new FixedClient("```markdown\n## Identity\nTest Co template, 16:9. The layouts carry the logo and the rule themselves.\n## Do and don't\n- Don't add logos.\n```");

        var guide = await BrandThemeAnalyzer.AnalyzeAsync(client, theme, store.TemplatePathOf(theme)!);

        Assert.StartsWith("## Identity", guide);
        Assert.DoesNotContain("```", guide);
        var facts = client.Asked.Last();
        Assert.Contains("Test Co", facts);
        Assert.Contains("a logo or picture", facts);           // what the layout draws by itself
        Assert.Contains("the sample slide puts \"Sub\" here", facts); // what a place is for
        Assert.Contains("[content slide]", facts);

        store.SaveRules(theme.Id, guide!);
        Assert.Equal(guide!.Trim(), store.ReadRules(theme.Id));
        Assert.Null(store.ReadRules("nothing"));
    }

    [Fact]
    public async Task An_empty_answer_gives_no_guide()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: false), "Test Co");

        Assert.Null(await BrandThemeAnalyzer.AnalyzeAsync(new FixedClient("  "), theme, store.TemplatePathOf(theme)!));
        Assert.Null(BrandThemeAnalyzer.Clean("too short"));
        Assert.True(BrandThemeAnalyzer.Clean(new string('x', 20000))!.Length < 6100);
    }

    [Fact]
    public void The_model_is_told_about_the_theme_in_use_and_nothing_for_the_built_in_one()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: true), "Test Co");

        var text = DeckRules.Compose(theme, "## Do and don't\n- Keep titles short.", new DateOnly(2026, 10, 21));

        Assert.Contains("Test Co", text);
        Assert.Contains("never add them", text);                  // logos, lines and page numbers come with the layouts
        Assert.Contains("the template's sample puts \"Sub\" there", text); // what the cover's first line is for
        Assert.Contains("Keep titles short.", text);              // the design guide
        Assert.Contains("Today is 2026/10/21", text);              // a date line is filled in, not left as a placeholder
        Assert.Contains("Never leave a placeholder", text);
        Assert.Contains("a section slide is just", text);
        Assert.DoesNotContain("design guide", DeckRules.Compose(theme, null)!);
        Assert.Null(DeckRules.Compose(BrandTheme.BuiltIn, "anything"));
    }
    [Fact]
    public void The_design_rules_outlive_deleting_the_theme_and_making_it_again()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var template = MakeTemplate(dir, withLogo: false);
        var theme = store.Import(template, "Test Co");
        store.SaveRules(theme.Id, "## Do and don't\n- My own rule.");

        store.Delete(theme.Id);
        Assert.Contains("My own rule.", store.ReadRules(theme.Id));      // the person's rules are not the theme's data

        var again = store.Import(template, "Test Co");                     // the same name: the same theme id
        Assert.Equal(theme.Id, again.Id);
        Assert.Contains("My own rule.", store.ReadRules(again.Id));
        store.Import(template, "Test Co");                                  // made again over itself
        Assert.Contains("My own rule.", store.ReadRules(again.Id));
    }

    [Fact]
    public void A_guide_kept_in_the_theme_folder_by_an_earlier_version_is_found()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var theme = store.Import(MakeTemplate(dir, withLogo: false), "Test Co");
        File.WriteAllText(Path.Combine(store.FolderOf(theme.Id), BrandThemeStore.RulesFile), "## Old place\n- Written by the first version.");

        Assert.Contains("Written by the first version.", store.ReadRules(theme.Id));
        Assert.True(File.Exists(store.RulesPathOf(theme.Id))); // and now kept apart
    }
    [Fact]
    public void A_theme_id_that_is_not_a_plain_folder_name_never_reaches_outside_the_themes_folder()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "themes");
        var store = new BrandThemeStore(root);
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

        foreach (var id in new[] { "..", "../outside", "..\\outside", "a/b", "C:\\Windows", "" })
        {
            Assert.True(store.Resolve(id).IsBuiltIn);
            store.Delete(id);                         // must not delete anything
        }

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Making_a_theme_again_replaces_it_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var store = new BrandThemeStore(Path.Combine(dir.Path, "themes"));
        var template = MakeTemplate(dir, withLogo: true);

        var first = store.Import(template, "Test Co");
        var second = store.Import(template, "Test Co");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(["default", first.Id], store.List().Select(t => t.Id));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(dir.Path, "themes")), d => Path.GetFileName(d).StartsWith('.') || Path.GetFileName(d).Contains(".old-"));
    }
    [Fact]
    public void What_a_template_says_cannot_break_out_of_the_preview_page()
    {
        using var dir = new TempDir();
        var template = MakeTemplate(dir, withLogo: false);
        using (var document = PresentationDocument.Open(template, true))
        {
            var fonts = document.PresentationPart!.SlideMasterParts.First().ThemePart!.Theme!.ThemeElements!.FontScheme!;
            fonts.MinorFont!.LatinFont!.Typeface = "Evil\"><script>alert(1)</script>";
            fonts.MajorFont!.LatinFont!.Typeface = "Other'; background:url(x)";
        }

        var theme = new BrandThemeStore(Path.Combine(dir.Path, "themes")).Import(template, "Evil");

        var all = string.Join(' ', new[] { theme.Title, theme.Content }.SelectMany(l => l.Placeholders).Select(p => p.Look.Font)
            .Concat(theme.Content.Shapes.Where(s => s.Text is not null).Select(s => s.Text!.Font)));
        Assert.DoesNotContain("\"", all);
        Assert.DoesNotContain("<", all);
        Assert.DoesNotContain(";", all);
        Assert.All(theme.Palette, color => Assert.Matches("^[0-9A-F]{6}$", color));
    }}
