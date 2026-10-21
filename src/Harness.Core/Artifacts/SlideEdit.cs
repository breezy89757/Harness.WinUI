// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;

namespace Harness.Core.Artifacts;

/// <summary>
/// What the person can do to a deck in the preview: change the text of a title, a line, a table cell or the notes, add or remove a
/// line, add, copy, remove or move a slide. The preview page says what happened as a small JSON message; this makes the new deck of it
/// (the deck stays the single source of truth, the page is told the result and draws it).
/// </summary>
public static class SlideEdit
{
    /// <param name="op">
    /// <c>{op:"text", slide, target:"title"|"subtitle"|"notes"|"block"|"cell", block, row, col, md}</c>, <c>{op:"insertAfter", slide, block}</c>,
    /// <c>{op:"removeBlock", slide, block}</c>, <c>{op:"addSlide"|"duplicateSlide"|"deleteSlide", slide}</c>, <c>{op:"moveSlide", slide, delta}</c>;
    /// slides are numbered from 1, blocks from 0.
    /// </param>
    /// <param name="newSlideTitle">The title a new slide starts with.</param>
    /// <returns>The deck after the edit; the same deck when the message doesn't make sense (so a stale page can't damage it).</returns>
    public static SlideDeck Apply(SlideDeck deck, JsonElement op, string newSlideTitle = "New slide")
    {
        if (op.ValueKind != JsonValueKind.Object || Text(op, "op") is not { } kind || Int(op, "slide") is not { } number)
            return deck;
        var slides = deck.Slides.ToList();
        var index = number - 1;
        if (index < 0 || index >= slides.Count)
            return deck;

        var slide = slides[index];
        switch (kind)
        {
            case "text":
                if (Edited(slide, op) is { } edited)
                    slides[index] = edited;
                else
                    return deck;
                break;

            case "insertAfter":
                {
                    var after = Int(op, "block") ?? -1;
                    if (after < -1 || after >= slide.Blocks.Count)
                        return deck;
                    var model = after >= 0 ? slide.Blocks[after] as BulletBlock : null;
                    var blocks = slide.Blocks.ToList();
                    blocks.Insert(after + 1, new BulletBlock(model?.Level ?? 0, model?.Numbered ?? false, [new TextRun(string.Empty)]));
                    slides[index] = slide with { Blocks = blocks };
                    break;
                }

            case "removeBlock":
                {
                    var at = Int(op, "block") ?? -1;
                    if (at < 0 || at >= slide.Blocks.Count)
                        return deck;
                    slides[index] = slide with { Blocks = [.. slide.Blocks.Where((_, i) => i != at)] };
                    break;
                }

            case "addSlide":
                slides.Insert(index + 1, new Slide(0, SlideKind.Content, newSlideTitle, null, [new BulletBlock(0, false, [new TextRun(string.Empty)])], null, true));
                break;

            case "duplicateSlide":
                slides.Insert(index + 1, slide);
                break;

            case "deleteSlide":
                if (slides.Count <= 1)
                    return deck;
                slides.RemoveAt(index);
                break;

            case "moveSlide":
                {
                    var to = index + Math.Sign(Int(op, "delta") ?? 0);
                    if (to < 0 || to >= slides.Count || to == index)
                        return deck;
                    slides.RemoveAt(index);
                    slides.Insert(to, slide);
                    break;
                }

            default:
                return deck;
        }

        return SlideDeck.From(slides);
    }

    private static Slide? Edited(Slide slide, JsonElement op)
    {
        var md = Text(op, "md") ?? string.Empty;
        switch (Text(op, "target"))
        {
            case "title":
                return slide with { Title = SlideDeck.PlainText(md) };
            case "subtitle":
                return slide with { Subtitle = SlideDeck.PlainText(md) is { Length: > 0 } subtitle ? subtitle : null };
            case "notes":
                return slide with { Notes = md.Trim().Length > 0 ? md.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() : null };
            case "block":
                {
                    var at = Int(op, "block") ?? -1;
                    if (at < 0 || at >= slide.Blocks.Count)
                        return null;
                    SlideBlock block = slide.Blocks[at] switch
                    {
                        BulletBlock bullet => bullet with { Runs = SlideDeck.Runs(md) },
                        ParagraphBlock => new ParagraphBlock(SlideDeck.Runs(md)),
                        var other => other,
                    };
                    return slide with { Blocks = [.. slide.Blocks.Select((b, i) => i == at ? block : b)] };
                }
            case "cell":
                {
                    var at = Int(op, "block") ?? -1;
                    var (row, col) = (Int(op, "row") ?? -1, Int(op, "col") ?? -1);
                    if (at < 0 || at >= slide.Blocks.Count || slide.Blocks[at] is not TableBlock table || row < 0 || row >= table.Rows.Count)
                        return null;
                    var cells = table.Rows[row].ToList();
                    if (col < 0 || col >= cells.Count)
                        return null;
                    cells[col] = SlideDeck.Runs(md);
                    var rows = table.Rows.Select((r, i) => i == row ? (IReadOnlyList<IReadOnlyList<TextRun>>)cells : r).ToList();
                    return slide with { Blocks = [.. slide.Blocks.Select((b, i) => i == at ? table with { Rows = rows } : b)] };
                }
            default:
                return null;
        }
    }

    private static string? Text(JsonElement op, string name) =>
        op.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement op, string name) =>
        op.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
