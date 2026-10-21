// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Artifacts;

/// <summary>
/// How big the text of a slide can be so it still fits in the theme's content area. The preview page and the .pptx are both made from
/// these numbers (in points), so a slide looks the same in both. Text is measured by estimate (a Chinese character is as wide as it is
/// high, other characters about half): good enough to choose a font size, not to lay out each line.
/// </summary>
public sealed class SlideLayout(BrandTheme theme)
{
    /// <summary>The layout of the built-in theme.</summary>
    public static SlideLayout Default { get; } = new(BrandTheme.BuiltIn);

    private const double IndentPerLevel = 28;
    private const double BulletIndent = 28;
    private static readonly double[] s_bodySizes = [28, 24, 22, 20, 18, 16, 14, 12];
    private static readonly double[] s_tableSizes = [18, 16, 14, 12, 11, 10, 9];

    private readonly Frame _content = theme.ContentFrame;

    public double Width => theme.Width;

    public double Height => theme.Height;

    public double ContentLeft => _content.X;

    public double ContentTop => _content.Y;

    public double ContentWidth => _content.W;

    public double ContentHeight => _content.H;

    /// <summary>The size of the slide's title: the layout's own size, made smaller when the title would take more lines than its frame is high.</summary>
    public double TitlePoints(Slide slide)
    {
        var layout = slide.Kind switch { SlideKind.Title => theme.Title, SlideKind.Section => theme.Section ?? theme.Title, _ => theme.Content };
        if (layout.Role("title") is not { } place)
            return 0;

        var size = place.Look.Size;
        while (size > 12 && Lines(slide.Title, place.Frame.W - 14, size) * size * 1.2 > place.Frame.H + 1)
            size -= 1;
        return size;
    }

    private static bool HasChart(Slide slide) => slide.Blocks.Any(b => b is ChartBlock);

    /// <summary>The width text and tables have: all of the content area, or the left part of it when a chart takes the right.</summary>
    public double TextWidth(Slide slide) => HasChart(slide) && slide.Blocks.Any(b => b is not ChartBlock) ? ContentWidth * 0.44 : ContentWidth;

    /// <summary>Where the slide's chart (the first one) goes: all of the content area when it is alone, else the right part; null when there is none.</summary>
    public Frame? ChartFrame(Slide slide)
    {
        if (!HasChart(slide))
            return null;
        return slide.Blocks.Any(b => b is not ChartBlock)
            ? new Frame(ContentLeft + ContentWidth * 0.47, ContentTop, ContentWidth * 0.53, ContentHeight)
            : _content;
    }

    /// <summary>The sizes of the lines under the title of a title or section slide (the subtitle, then who and when), made smaller when they would take more lines than their frame is high.</summary>
    public (double First, double Second) LinePoints(Slide slide)
    {
        if (slide.Kind == SlideKind.Content)
            return (0, 0);
        var layout = slide.Kind == SlideKind.Title ? theme.Title : theme.Section ?? theme.Title;
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(slide.Subtitle))
            lines.Add(slide.Subtitle);
        if (slide.Kind == SlideKind.Title)
            lines.AddRange(slide.Blocks.Select(b => b is BulletBlock bullet ? SlideDeck.TextOf(bullet.Runs) : b is ParagraphBlock paragraph ? SlideDeck.TextOf(paragraph.Runs) : string.Empty));

        var first = layout.Role("sub1");
        var second = layout.Role("sub2");
        return (Fit(second is null ? lines : lines.Take(1), first), Fit(lines.Skip(1), second));
    }

    private static double Fit(IEnumerable<string> texts, ThemePlaceholder? place)
    {
        if (place is null)
            return 0;
        var list = texts.ToList();
        var size = place.Look.Size;
        while (size > 10 && list.Sum(t => Lines(t, place.Frame.W - 14, size)) * size * 1.2 > place.Frame.H + 1)
            size -= 1;
        return size;
    }

    /// <summary>The size of the text (paragraphs and lists) of a content slide.</summary>
    public double BodyPoints(Slide slide)
    {
        if (slide.Kind != SlideKind.Content)
            return 24;

        var table = TablePoints(slide);
        var available = ContentHeight - (table > 0 ? TableHeight(slide, table) + (HasText(slide) ? 12 : 0) : 0);
        var sizes = s_bodySizes.Where(s => s <= Math.Max(theme.BodyMax, s_bodySizes[^1])).ToList();
        foreach (var size in sizes)
        {
            if (TextHeight(slide, size) <= available)
                return size;
        }

        return sizes[^1];
    }

    /// <summary>The size of the text in a table, or 0 when the slide has none.</summary>
    public double TablePoints(Slide slide)
    {
        if (slide.Kind != SlideKind.Content || slide.Blocks.OfType<TableBlock>().FirstOrDefault() is null)
            return 0;

        var reserved = HasText(slide) ? TextHeight(slide, s_bodySizes[^1]) + 12 : 0;
        foreach (var size in s_tableSizes.Where(s => s <= Math.Max(theme.BodyMax, s_tableSizes[^1])))
        {
            if (TableHeight(slide, size) <= ContentHeight - reserved)
                return size;
        }

        return s_tableSizes[^1];
    }

    private static bool HasText(Slide slide) => slide.Blocks.Any(b => b is not TableBlock);

    /// <summary>The height of the paragraphs and list items at <paramref name="size"/>.</summary>
    public double TextHeight(Slide slide, double size)
    {
        double height = 0;
        foreach (var block in slide.Blocks)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    height += Lines(SlideDeck.TextOf(paragraph.Runs), TextWidth(slide), size) * size * 1.3 + size * 0.4;
                    break;
                case BulletBlock bullet:
                    var width = TextWidth(slide) - BulletIndent - bullet.Level * IndentPerLevel;
                    var itemSize = bullet.Level == 0 ? size : size * 0.9;
                    height += Lines(SlideDeck.TextOf(bullet.Runs), width, itemSize) * itemSize * 1.3 + itemSize * 0.4;
                    break;
            }
        }

        return height;
    }

    /// <summary>The height of the first table of the slide at <paramref name="size"/>.</summary>
    public double TableHeight(Slide slide, double size)
    {
        if (slide.Blocks.OfType<TableBlock>().FirstOrDefault() is not { } table || table.Rows.Count == 0)
            return 0;

        var columns = Math.Max(1, table.Rows.Max(r => r.Count));
        var inner = TextWidth(slide) / columns - 14;
        return table.Rows.Sum(row => Math.Max(1, row.Select(cell => Lines(SlideDeck.TextOf(cell), inner, size)).DefaultIfEmpty(1).Max()) * size * 1.25 + 12);
    }

    /// <summary>How many lines <paramref name="text"/> takes in a box that is <paramref name="width"/> wide.</summary>
    public static int Lines(string text, double width, double size)
    {
        if (text.Length == 0)
            return 1;
        var ems = text.Sum(Em);
        return Math.Max(1, (int)Math.Ceiling(ems * size * 1.06 / width));
    }

    private static double Em(char c) => c switch
    {
        ' ' => 0.3,
        >= '⺀' and <= '鿿' => 1.0,
        >= '가' and <= '힯' => 1.0,
        >= '＀' and <= '￯' => 1.0,
        _ => 0.55,
    };
}
