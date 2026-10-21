// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace Harness.Core.Artifacts;

/// <summary>
/// Has the model read a company template and write its design guide: what the logos and lines are for, what each kind of slide carries
/// by itself, what the lines under the cover title are for, how text and tables look, and what not to do. The facts it works from are
/// read by the program (<see cref="BrandThemeExtractor"/>: positions, colours, fonts, the template's own sample slides); the model
/// only turns them into rules for another model, so it can't get a position wrong. The guide is a Markdown file the person can edit,
/// and it is part of the instructions whenever the theme is in use (see <see cref="DeckRules"/>).
/// </summary>
public static class BrandThemeAnalyzer
{
    private const int MaxGuideChars = 6000;

    private const string Instructions = """
        You write the design guide of a company's slide template. Another AI will read your guide before it writes presentations that are
        drawn and exported in this template, so write what it needs to know, as short rules.

        You are given the facts the program read from the template: slide size, what each layout draws by itself (pictures, lines, page
        number), the text places of each layout with their size, font and what the template's own sample slide put there, and the text of
        the sample slides. Use only these facts. Where they don't say something, write "unknown" or leave it out; don't invent anything
        about the company.

        Write Markdown in the language of the template's text (Traditional Chinese for a Chinese template), under 60 lines, with these sections:
        ## Identity: whose template it is (from the names in it), the slide size.
        ## Layouts: for the title (cover), content and section slide: what it shows by itself (logos, lines, footer, page number), so the writer does not add them, and what it must provide.
        ## Cover lines: what each text place under the cover title is for (from the sample slide's text) and the format it expects (a date format, a name), so the writer fills each one correctly.
        ## Text and tables: fonts, the size range, bullets, the table header colour, the longest title that fits on one line.
        ## Do and don't: short rules that follow from the facts (don't add logos or footers the layout already has; don't use colours that aren't the template's; keep titles short enough).
        Output only the guide.
        """;

    /// <summary>The guide for <paramref name="theme"/>, or null when the model gave nothing usable.</summary>
    /// <param name="templatePath">The theme's template file (its sample slides are read for hints).</param>
    public static async Task<string?> AnalyzeAsync(IChatClient client, BrandTheme theme, string templatePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var facts = Facts(theme, File.Exists(templatePath) ? BrandThemeExtractor.SampleSlides(templatePath) : []);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, facts)], cancellationToken: cancellationToken).ConfigureAwait(false);
        return Clean(response.Text);
    }

    /// <summary>The facts about a theme, as text for the model.</summary>
    public static string Facts(BrandTheme theme, IReadOnlyList<(string Layout, string Text)> samples)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Template name: {theme.Name}\nSlide size: {theme.Width:0.#} x {theme.Height:0.#} pt ({(theme.Width / theme.Height > 1.5 ? "16:9" : "4:3")})\n");
        text.Append(CultureInfo.InvariantCulture, $"Ink colour (bullets and table headers): #{theme.Accent}; body text up to {theme.BodyMax:0} pt\n");

        void Layout(string label, ThemeLayout? layout)
        {
            if (layout is null)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n[{label}] none (a section slide uses the title layout)\n");
                return;
            }

            text.Append(CultureInfo.InvariantCulture, $"\n[{label}] layout \"{layout.Name}\"\n  drawn by the layout itself:\n");
            foreach (var shape in layout.Shapes)
            {
                text.Append(CultureInfo.InvariantCulture, $"   - {shape.Kind} at x={shape.Frame.X:0} y={shape.Frame.Y:0} size {shape.Frame.W:0}x{shape.Frame.H:0}");
                if (shape.Color is not null)
                    text.Append(CultureInfo.InvariantCulture, $" colour #{shape.Color}");
                if (shape.Kind == "image")
                    text.Append(" (a logo or picture)");
                text.Append('\n');
            }

            text.Append("  text places:\n");
            foreach (var place in layout.Placeholders)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"   - {place.Role}: x={place.Frame.X:0} y={place.Frame.Y:0} size {place.Frame.W:0}x{place.Frame.H:0}, {place.Look.Size:0.#} pt {(place.Look.Bold ? "bold" : "regular")}, {place.Look.Align}, font {place.Look.Latin ?? place.Look.Ea ?? place.Look.Font}");
                if (place.Hint is not null)
                    text.Append(CultureInfo.InvariantCulture, $", the sample slide puts \"{place.Hint}\" here");
                text.Append('\n');
            }
        }

        Layout("title slide", theme.Title);
        Layout("content slide", theme.Content);
        Layout("section slide", theme.Section);

        if (samples.Count > 0)
        {
            text.Append("\nText of the template's own sample slides:\n");
            foreach (var (layout, sample) in samples.Take(4))
                text.Append(CultureInfo.InvariantCulture, $" - on layout \"{layout}\": {sample}\n");
        }

        return text.ToString();
    }

    /// <summary>The guide without a code fence around it, or null when there is nothing in it; very long guides are cut.</summary>
    public static string? Clean(string? text)
    {
        var guide = (text ?? string.Empty).Trim();
        if (guide.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = guide.IndexOf('\n');
            guide = firstLine < 0 ? string.Empty : guide[(firstLine + 1)..];
            if (guide.TrimEnd().EndsWith("```", StringComparison.Ordinal))
                guide = guide.TrimEnd()[..^3];
            guide = guide.Trim();
        }

        if (guide.Length < 40)
            return null;
        return guide.Length > MaxGuideChars ? guide[..MaxGuideChars].TrimEnd() + "\n…" : guide;
    }
}
