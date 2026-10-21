// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Harness.Core.Artifacts;

public enum SlideKind
{
    /// <summary>The first slide: the deck's title, a subtitle and a line or two (who, when).</summary>
    Title,

    /// <summary>A slide with only a heading (and maybe a subtitle) that opens a part of the deck.</summary>
    Section,

    Content,
}

/// <summary>A piece of text with the same look: <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c> in the source.</summary>
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Code = false);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ParagraphBlock), "p")]
[JsonDerivedType(typeof(BulletBlock), "b")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(ChartBlock), "chart")]
public abstract record SlideBlock;

public sealed record ParagraphBlock(IReadOnlyList<TextRun> Runs) : SlideBlock;

/// <param name="Level">0 for a top-level item, 1 and more for items inside it.</param>
public sealed record BulletBlock(int Level, bool Numbered, IReadOnlyList<TextRun> Runs) : SlideBlock;

/// <param name="Rows">Every row's cells; the first row is the header when <paramref name="HasHeader"/>.</param>
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<TextRun>>> Rows, bool HasHeader) : SlideBlock;

/// <param name="Number">Counting from 1.</param>
/// <param name="IsComplete">False for the slide that is still being written.</param>
public sealed record Slide(int Number, SlideKind Kind, string Title, string? Subtitle, IReadOnlyList<SlideBlock> Blocks, string? Notes, bool IsComplete);

/// <summary>
/// A presentation written as text, the way the model writes a <c>slides</c> artifact (see the model's
/// instructions): slides are separated by a line with only <c>---</c>; in a slide <c># Title</c> is the title, <c>## </c> a subtitle
/// (or sub-heading), <c>- </c> and <c>1. </c> list items (indent to nest), <c>| a | b |</c> table rows, and <c>Notes:</c> starts the
/// speaker notes. Because it is plain structure, the preview can show each slide as soon as it is written and the same text becomes a
/// real, editable .pptx (see <see cref="Files.PowerPointWriter"/>).
/// </summary>
public sealed partial class SlideDeck
{
    private SlideDeck(IReadOnlyList<Slide> slides) => Slides = slides;

    public IReadOnlyList<Slide> Slides { get; }

    /// <param name="isComplete">False while the text is still streaming in: the last slide is then marked as not complete.</param>
    public static SlideDeck Parse(string content, bool isComplete)
    {
        var chunks = new List<List<string>> { new() };
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim() == "---")
                chunks.Add([]);
            else
                chunks[^1].Add(line);
        }

        // A deck that starts with --- (or has two in a row) has an empty piece there; it is not a slide.
        var kept = chunks.Select((lines, index) => (lines, index)).Where(c => c.lines.Any(l => l.Trim().Length > 0)).ToList();
        var slides = new List<Slide>();
        for (var i = 0; i < kept.Count; i++)
        {
            var (lines, index) = kept[i];
            var complete = isComplete || index < chunks.Count - 1;
            slides.Add(ParseSlide(lines, i + 1, isFirst: i == 0, complete));
        }

        return new SlideDeck(slides);
    }

    private static Slide ParseSlide(List<string> lines, int number, bool isFirst, bool complete)
    {
        string title = string.Empty;
        string? subtitle = null;
        var blocks = new List<SlideBlock>();
        var notes = new List<string>();
        var inNotes = false;
        var indents = new List<int>(); // the indents of the list levels met so far in the current list
        List<List<IReadOnlyList<TextRun>>>? tableRows = null;
        var tableHeader = false;
        List<string>? chartLines = null; // inside a chart block: its JSON, up to the closing fence

        void AddChart()
        {
            if (ChartBlock.TryParse(string.Join('\n', chartLines!), out var chart, out var error))
                blocks.Add(chart!);
            else
                blocks.Add(new ParagraphBlock([new TextRun($"[chart: {error}]", Italic: true)]));
            chartLines = null;
        }

        void EndTable()
        {
            if (tableRows is { Count: > 0 })
                blocks.Add(new TableBlock(tableRows.Select(r => (IReadOnlyList<IReadOnlyList<TextRun>>)r).ToList(), tableHeader));
            tableRows = null;
            tableHeader = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (inNotes)
            {
                notes.Add(line);
                continue;
            }

            if (chartLines is not null)
            {
                if (line.Trim() == "```")
                    AddChart();
                else
                    chartLines.Add(raw);
                continue;
            }

            if (line.TrimStart().StartsWith("```chart", StringComparison.OrdinalIgnoreCase))
            {
                EndTable();
                indents.Clear();
                chartLines = [];
                continue;
            }

            if (line.Trim().Length == 0)
            {
                EndTable();
                indents.Clear();
                continue;
            }

            if (NotesStart().Match(line) is { Success: true } start)
            {
                EndTable();
                inNotes = true;
                if (start.Groups["rest"].Value.Trim().Length > 0)
                    notes.Add(start.Groups["rest"].Value.Trim());
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('|'))
            {
                indents.Clear();
                tableRows ??= [];
                if (TableSeparator().IsMatch(trimmed))
                    tableHeader = tableRows.Count == 1;
                else
                    tableRows.Add([.. SplitRow(trimmed).Select(Runs)]);
                continue;
            }

            EndTable();

            if (trimmed.StartsWith("# ", StringComparison.Ordinal) && title.Length == 0)
            {
                title = Plain(trimmed[2..]);
                continue;
            }
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var text = trimmed[3..].Trim();
                if (subtitle is null && (isFirst || blocks.Count == 0))
                    subtitle = Plain(text);
                else
                    blocks.Add(new ParagraphBlock([new TextRun(Plain(text), Bold: true)]));
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                var text = trimmed.TrimStart('#').Trim();
                if (title.Length == 0)
                    title = Plain(text);
                else if (text.Length > 0)
                    blocks.Add(new ParagraphBlock([new TextRun(Plain(text), Bold: true)]));
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                var indent = item.Groups["indent"].Value.Replace("\t", "    ").Length;
                while (indents.Count > 0 && indents[^1] > indent)
                    indents.RemoveAt(indents.Count - 1);
                if (indents.Count == 0 || indents[^1] < indent)
                    indents.Add(indent);
                var numbered = char.IsDigit(item.Groups["mark"].Value[0]);
                blocks.Add(new BulletBlock(Math.Min(indents.Count - 1, 3), numbered, Runs(item.Groups["text"].Value)));
                continue;
            }

            indents.Clear();
            if (Image().IsMatch(trimmed))
                continue; // pictures aren't part of a deck (yet)
            if (trimmed.StartsWith('>'))
                trimmed = trimmed.TrimStart('>').TrimStart(); // a quote is just a line here
            if (trimmed.Length > 0)
                blocks.Add(new ParagraphBlock(Runs(trimmed)));
        }

        EndTable();
        if (chartLines is not null && complete)
            AddChart(); // the closing fence never came
        var noteText = string.Join('\n', notes).Trim();
        var kind = isFirst ? SlideKind.Title : blocks.Count == 0 && title.Length > 0 ? SlideKind.Section : SlideKind.Content;
        return new Slide(number, kind, title, subtitle, blocks, noteText.Length == 0 ? null : noteText, complete);
    }

    private static IEnumerable<string> SplitRow(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|'))
            inner = inner[1..];
        if (inner.EndsWith('|'))
            inner = inner[..^1];
        return EscapedPipe().Split(inner).Select(c => c.Replace("\\|", "|", StringComparison.Ordinal).Trim());
    }

    /// <summary>Text with the Markdown marks turned into runs; links keep their text, the rest is plain.</summary>
    public static IReadOnlyList<TextRun> Runs(string text)
    {
        text = Link().Replace(text, "$1");
        var runs = new List<TextRun>();
        var position = 0;
        foreach (Match mark in Emphasis().Matches(text))
        {
            if (mark.Index > position)
                runs.Add(new TextRun(text[position..mark.Index]));
            if (mark.Groups["bold"].Success)
                runs.Add(new TextRun(mark.Groups["bold"].Value, Bold: true));
            else if (mark.Groups["italic"].Success)
                runs.Add(new TextRun(mark.Groups["italic"].Value, Italic: true));
            else
                runs.Add(new TextRun(mark.Groups["code"].Value, Code: true));
            position = mark.Index + mark.Length;
        }

        if (position < text.Length)
            runs.Add(new TextRun(text[position..]));
        return runs.Count == 0 ? [new TextRun(string.Empty)] : runs;
    }

    /// <summary>A deck of these slides: numbered again, and the first one is the title slide, one with only a heading is a section.</summary>
    public static SlideDeck From(IEnumerable<Slide> slides) => new(
        [.. slides.Select((s, i) => s with
        {
            Number = i + 1,
            Kind = i == 0 ? SlideKind.Title : s.Blocks.Count == 0 && s.Title.Length > 0 ? SlideKind.Section : SlideKind.Content,
            IsComplete = true,
        })]);

    /// <summary>The deck as the text <see cref="Parse"/> reads (what is stored as the artifact after the person edited it).</summary>
    public string ToMarkdown() => string.Join("\n---\n", Slides.Select(SlideMarkdown)) + "\n";

    private static string SlideMarkdown(Slide slide)
    {
        var text = new System.Text.StringBuilder();
        text.Append("# ").Append(slide.Title).Append('\n');
        if (slide.Subtitle is { Length: > 0 } subtitle)
            text.Append("## ").Append(subtitle).Append('\n');

        var counters = new int[8];
        TableBlock? previousTable = null;
        foreach (var block in slide.Blocks)
        {
            if (previousTable is not null && block is not TableBlock)
                text.Append('\n');
            switch (block)
            {
                case ParagraphBlock paragraph:
                    Array.Clear(counters);
                    text.Append(Marked(paragraph.Runs)).Append('\n');
                    break;
                case BulletBlock bullet:
                    var level = Math.Clamp(bullet.Level, 0, counters.Length - 1);
                    Array.Clear(counters, level + 1, counters.Length - level - 1);
                    var mark = bullet.Numbered ? $"{++counters[level]}." : "-";
                    if (!bullet.Numbered)
                        counters[level] = 0;
                    text.Append(' ', level * 2).Append(mark).Append(' ').Append(Marked(bullet.Runs)).Append('\n');
                    break;
                case ChartBlock chart:
                    Array.Clear(counters);
                    text.Append("```chart\n").Append(chart.ToDeckJson()).Append("\n```\n");
                    break;
                case TableBlock table:
                    Array.Clear(counters);
                    text.Append('\n');
                    for (var r = 0; r < table.Rows.Count; r++)
                    {
                        text.Append("| ").Append(string.Join(" | ", table.Rows[r].Select(cell => Marked(cell).Replace("|", "\\|", StringComparison.Ordinal)))).Append(" |\n");
                        if (r == 0 && table.HasHeader)
                            text.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", table.Rows[0].Count))).Append('\n');
                    }
                    break;
            }
            previousTable = block as TableBlock;
        }

        if (slide.Notes is { Length: > 0 } notes)
        {
            if (previousTable is not null)
                text.Append('\n');
            text.Append("Notes: ").Append(notes.Replace("\n", "\n", StringComparison.Ordinal)).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Runs as the marks the model writes them with.</summary>
    public static string Marked(IEnumerable<TextRun> runs) => string.Concat(runs.Select(r =>
        r.Text.Length == 0 ? string.Empty
        : r.Code ? $"`{r.Text}`"
        : r.Bold ? $"**{r.Text}**"
        : r.Italic ? $"*{r.Text}*"
        : r.Text));

    /// <summary>The characters of a text written with marks (a title, a subtitle), without the marks.</summary>
    public static string PlainText(string text) => Plain(text);

    private static string Plain(string text) => string.Concat(Runs(text).Select(r => r.Text)).Trim();

    /// <summary>The characters of a run list, without the marks.</summary>
    public static string TextOf(IEnumerable<TextRun> runs) => string.Concat(runs.Select(r => r.Text));

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // not WhenWritingDefault: the title kind is the enum's 0
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The deck for the preview page: every slide with the font sizes the preview and the .pptx share (see <see cref="SlideLayout"/>), and
    /// the theme it is drawn in (the built-in one when <paramref name="theme"/> is null).
    /// </summary>
    public string ToJson(BrandTheme? theme = null)
    {
        theme ??= BrandTheme.BuiltIn;
        var layout = new SlideLayout(theme);
        return JsonSerializer.Serialize(
            new
            {
                theme,
                imageBase = theme.IsBuiltIn ? string.Empty : $"_themes/{theme.Id}/",
                slides = Slides.Select(s => new
                {
                    s.Number, s.Kind, s.Title, s.Subtitle, s.Blocks, s.Notes, s.IsComplete,
                    titlePt = layout.TitlePoints(s),
                    sub1Pt = layout.LinePoints(s).First,
                    sub2Pt = layout.LinePoints(s).Second,
                    bodyPt = layout.BodyPoints(s),
                    tablePt = layout.TablePoints(s),
                    textWidth = layout.TextWidth(s),
                    chartFrame = layout.ChartFrame(s),
                }),
            },
            s_json);
    }
    [GeneratedRegex(@"^(?:notes|speaker notes|備註|講者備註)\s*[:：]\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotesStart();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<mark>[-*•]|\d+[.)])(?:\s+(?<text>.*))?$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^!\[[^\]]*\]\([^)]*\)$")]
    private static partial Regex Image();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex EscapedPipe();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|(?<!\*)\*(?<italic>[^*\s](?:[^*]*?[^*\s])?)\*(?!\*)|`(?<code>[^`]+)`")]
    private static partial Regex Emphasis();
}
