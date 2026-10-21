// Harness.WinUI — Licensed under the MIT License.

using System.Text;

namespace Harness.Core.Artifacts;

/// <summary>
/// What the model is told about the company theme in use, so a deck it writes follows the template: that the layouts already carry the
/// logos, lines and page numbers, what to write on the cover, and the theme's design guide.
/// </summary>
public static class DeckRules
{
    /// <summary>Instructions to add while <paramref name="theme"/> is the theme in use; null for the built-in theme.</summary>
    /// <param name="guide">The design guide (see <see cref="BrandThemeAnalyzer"/>), or null when the theme has none yet.</param>
    /// <param name="today">Today's date, so a date line on the cover can be filled in rather than left as a placeholder.</param>
    public static string? Compose(BrandTheme theme, string? guide, DateOnly? today = null)
    {
        if (theme.IsBuiltIn)
            return null;

        var text = new StringBuilder();
        text.Append($"## Company presentation theme: {theme.Name}\n");
        text.Append("A slides artifact is drawn and exported in this company's own template. Its layouts already carry the company's logos, lines, ");
        text.Append("footers and page numbers: never add them, and don't write slide numbers or company names as filler. ");

        var first = theme.Title.Role("sub1");
        var second = theme.Title.Role("sub2");
        text.Append("The first slide is the cover: \"# title\"");
        if (first is not null)
            text.Append($", then \"## …\" for the cover's first line{Describe(first)}");
        if (second is not null)
            text.Append($", then one more line for the next place{Describe(second)}");
        text.Append(". Only the cover has these lines: a section slide is just \"# heading\" and at most one \"## …\" line, with no presenter or date. ");
        text.Append(today is { } date
            ? $"Today is {date:yyyy/MM/dd}: use it for a date line unless the user gave another date. "
            : "If you don't know a date, leave that line out. ");
        text.Append("Never leave a placeholder such as YYYY/MM/DD or Year/Month/Day. Keep titles short: the title place is about ")
            .Append(Chars(theme.Content.Role("title"))).Append(" characters wide on a content slide.\n");

        if (!string.IsNullOrWhiteSpace(guide))
            text.Append("\nThe company's design guide:\n").Append(guide.Trim()).Append('\n');
        return text.ToString().TrimEnd();
    }

    private static string Describe(ThemePlaceholder place) => place.Hint is { Length: > 0 } hint ? $" (the template's sample puts \"{hint}\" there)" : string.Empty;

    private static string Chars(ThemePlaceholder? title) =>
        title is null ? "20" : Math.Max(8, (int)(title.Frame.W / Math.Max(title.Look.Size, 1))).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
