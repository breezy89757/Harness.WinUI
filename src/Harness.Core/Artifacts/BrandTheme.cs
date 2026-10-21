// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Core.Artifacts;

/// <summary>A rectangle on the slide, in points (1 pt = 12700 EMU), from the top left corner.</summary>
public sealed record Frame(double X, double Y, double W, double H);

/// <param name="Color">RRGGBB, without #.</param>
/// <param name="Align">left, center or right.</param>
/// <param name="Anchor">top, middle or bottom: where the text sits in its frame.</param>
/// <param name="Font">A CSS font-family list (the Chinese and the Latin font of the template).</param>
/// <param name="Latin">The Latin font the template sets for this text itself, null when it leaves it to the theme.</param>
/// <param name="Ea">Likewise for Chinese, Japanese and Korean characters.</param>
public sealed record TextLook(double Size, bool Bold, string Color, string Align, string Anchor, string Font, string? Latin = null, string? Ea = null);

/// <summary>
/// Something a layout or the slide master draws on every slide that uses it: a picture (a logo), a line, a filled rectangle, or the
/// page number.
/// </summary>
/// <param name="Kind">image, line, rect or pageNumber.</param>
/// <param name="Image">For an image: the file name in the theme's folder.</param>
/// <param name="Color">For a line or a rectangle: RRGGBB.</param>
/// <param name="Weight">For a line: its thickness in points.</param>
/// <param name="Text">For the page number: how it looks.</param>
public sealed record StaticShape(string Kind, Frame Frame, string? Image = null, string? Color = null, double Weight = 0, TextLook? Text = null);

/// <summary>A placeholder of a layout: where text of a slide goes and how it looks until the slide says otherwise.</summary>
/// <param name="Role">title, sub1 and sub2 (the lines under the title of a title slide), body.</param>
/// <param name="PhType">The placeholder's type in the file (title, ctrTitle, body, subTitle …), null when it has none.</param>
/// <param name="PhIdx">Its index in the file, null when it has none.</param>
/// <param name="Hint">What the template's own sample slide puts there ("Presenter", "Year/Month/Day"): what the place is for.</param>
public sealed record ThemePlaceholder(string Role, string? PhType, int? PhIdx, Frame Frame, TextLook Look, string? Hint = null);

/// <param name="PartName">Where the layout is in the template file, so slides of an exported file can use it.</param>
public sealed record ThemeLayout(string Name, string PartName, IReadOnlyList<StaticShape> Shapes, IReadOnlyList<ThemePlaceholder> Placeholders)
{
    public ThemePlaceholder? Role(string role) => Placeholders.FirstOrDefault(p => p.Role == role);
}

/// <summary>
/// How a deck looks for one company: the slide size, the logos, lines and page number every slide carries, where titles and text go and
/// how they look. Made from the company's own .pptx template (see <see cref="BrandThemeExtractor"/>) and kept as data in the user's
/// folder, so nothing of a company's brand is part of the program. The preview draws from it, and an exported deck is made on the
/// template itself, so it has the company's real slide master.
/// </summary>
/// <param name="Id">Folder name under the themes folder.</param>
/// <param name="Width">Slide width in points.</param>
/// <param name="BodyMax">The largest size the text of a content slide may have (what the template uses for its text).</param>
/// <param name="Template">File name of the template copy in the theme's folder; null for the built-in theme.</param>
/// <param name="Section">null when the template has no section layout (a section slide then uses the title layout).</param>
public sealed record BrandTheme(
    string Id, string Name, double Width, double Height, double BodyMax, ThemeLayout Title, ThemeLayout Content, ThemeLayout? Section, string? Template)
{
    /// <summary>The built-in theme's id.</summary>
    public const string DefaultId = "default";

    /// <summary>The area of a content slide where the text and tables go.</summary>
    public Frame ContentFrame => Content.Role("body")?.Frame ?? new Frame(48, 112, Width - 96, Height - 160);

    public bool IsBuiltIn => Template is null;

    /// <summary>The colour of bullets and table headers: the built-in blue, or the ink colour of the template's text.</summary>
    public string Accent => IsBuiltIn ? "1F4E79" : Content.Role("body")?.Look.Color ?? "000000";

    /// <summary>The colour of text: the built-in dark grey, or the template's.</summary>
    public string Ink => IsBuiltIn ? "1F2937" : Content.Role("body")?.Look.Color ?? "000000";

    /// <summary>Colours for chart series (for a pie, its slices): the built-in set, or the template's brand colour (the colour of its rule) with its ink and greys.</summary>
    public IReadOnlyList<string> Palette
    {
        get
        {
            if (IsBuiltIn)
                return ["1F4E79", "2E75B6", "548235", "BF9000", "C55A11", "7030A0"];

            var brand = Content.Shapes.Concat(Title.Shapes).FirstOrDefault(s => s.Kind is "line" or "rect" && s.Color is not null)?.Color ?? Ink;
            return [brand, Ink, Darker(brand, 0.6), "7F7F7F", "BFBFBF", Darker(brand, 0.35)];
        }
    }

    /// <summary>The colour scaled towards black (factor under 1); text that isn't a colour is returned as it is.</summary>
    private static string Darker(string rgb, double factor)
    {
        if (rgb.Length != 6 || !int.TryParse(rgb, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return rgb;
        int Part(int shift) => (int)(((value >> shift) & 0xFF) * factor);
        return $"{Part(16):X2}{Part(8):X2}{Part(0):X2}";
    }

    /// <summary>The fonts the text of a content slide is written in when it has to say so itself (a table; a template whose text styles aren't its brand fonts).</summary>
    public (string? Latin, string? Ea) BodyFonts => (Content.Role("body")?.Look.Latin ?? Content.Role("title")?.Look.Latin, Content.Role("body")?.Look.Ea ?? Content.Role("title")?.Look.Ea);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static BrandTheme? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BrandTheme>(json, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The theme drawn when the person has none: white, a blue rule under titles, the geometry the built-in .pptx uses.</summary>
    public static BrandTheme BuiltIn { get; } = CreateBuiltIn();

    private static BrandTheme CreateBuiltIn()
    {
        const string font = "Calibri,'Microsoft JhengHei','Segoe UI',sans-serif";
        TextLook Look(double size, bool bold, string color, string align, string anchor) => new(size, bold, color, align, anchor, font);

        var number = new StaticShape("pageNumber", new Frame(840, 500, 72, 24), Text: Look(12, false, "6B7280", "right", "middle"));
        var content = new ThemeLayout("Title and Content", string.Empty,
            [new StaticShape("line", new Frame(48, 100, 864, 0), Color: PowerPointAccent, Weight: 3), number],
            [new ThemePlaceholder("title", "title", null, new Frame(48, 24, 864, 72), Look(32, true, "1F2937", "left", "bottom")),
             new ThemePlaceholder("body", null, 1, new Frame(48, 112, 864, 380), Look(28, false, "1F2937", "left", "top"))]);
        var title = new ThemeLayout("Title Slide", string.Empty,
            [new StaticShape("line", new Frame(72, 284, 120, 0), Color: PowerPointAccent, Weight: 4)],
            [new ThemePlaceholder("title", "ctrTitle", null, new Frame(72, 150, 816, 120), Look(44, true, "1F2937", "left", "bottom")),
             new ThemePlaceholder("sub1", "subTitle", 1, new Frame(72, 298, 816, 60), Look(24, false, "6B7280", "left", "top")),
             new ThemePlaceholder("sub2", null, null, new Frame(72, 372, 816, 100), Look(18, false, "6B7280", "left", "top"))]);
        var section = new ThemeLayout("Section Header", string.Empty,
            [new StaticShape("line", new Frame(72, 314, 120, 0), Color: PowerPointAccent, Weight: 4)],
            [new ThemePlaceholder("title", "title", null, new Frame(72, 200, 816, 100), Look(40, true, "1F2937", "left", "bottom")),
             new ThemePlaceholder("sub1", "body", 1, new Frame(72, 326, 816, 60), Look(22, false, "6B7280", "left", "top"))]);
        return new BrandTheme(DefaultId, "Harness", 960, 540, 28, title, content, section, null);
    }

    private const string PowerPointAccent = "1F4E79";
}
