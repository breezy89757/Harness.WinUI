// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace Harness.Core.Artifacts;

/// <summary>
/// Reads a company's .pptx template and makes a <see cref="BrandTheme"/> of it: the slide size; what the slide master and the layouts
/// draw on every slide (logos, lines, the page number); and where the title and the text go and how they look on a title slide, a
/// content slide and a section slide. The template is copied, with its pictures, into the theme's folder: the program keeps no brand of
/// its own. Looks are found the way PowerPoint finds them: the layout's own style, then the master's, then the master's text styles.
/// </summary>
public static class BrandThemeExtractor
{
    public const string TemplateFile = "template.pptx";

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const double EmuPerPoint = 12700;

    /// <param name="folder">The theme's folder; the template and its pictures are written there.</param>
    /// <exception cref="InvalidDataException">The file isn't a presentation with a slide master.</exception>
    public static BrandTheme FromPptx(string pptxPath, string id, string name, string folder)
    {
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, TemplateFile);
        if (!string.Equals(Path.GetFullPath(pptxPath), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            File.Copy(pptxPath, copy, overwrite: true);

        try
        {
            using var document = PresentationDocument.Open(copy, false);
            var presentation = document.PresentationPart ?? throw new InvalidDataException("This isn't a PowerPoint presentation.");
            var master = presentation.SlideMasterParts.FirstOrDefault() ?? throw new InvalidDataException("The presentation has no slide master.");
            var size = presentation.Presentation?.SlideSize ?? throw new InvalidDataException("The presentation has no slide size.");

            var masterXml = Load(master);
            var palette = Palette.From(master, masterXml);
            var masterShapes = Shapes(masterXml, master, folder, palette).ToList();
            var layouts = master.SlideLayoutParts.Select(part => Describe(part, masterXml, palette, masterShapes, folder)).ToList();
            if (layouts.Count == 0)
                throw new InvalidDataException("The slide master has no layouts.");

            // What the template's own sample slides put in each place tells what the place is for.
            var samples = SampleTexts(presentation);
            layouts = [.. layouts.Select(l => l with { Layout = WithHints(l.Layout, samples) })];

            var title = PickTitle(layouts);
            var content = PickContent(layouts, title);
            var section = layouts.FirstOrDefault(l => l.Type == "secHead" || NameHas(l.Name, "section", "章節", "區段", "分隔"))?.Layout;
            var bodyLook = content.Layout.Role("body")?.Look;
            return new BrandTheme(
                id, name, size.Cx!.Value / EmuPerPoint, size.Cy!.Value / EmuPerPoint,
                Math.Clamp(Math.Max(bodyLook?.Size ?? 18, 18), 18, 20), Prune(title.Layout, "body"), Prune(content.Layout, "sub1", "sub2"), section is null ? null : Prune(section, "body", "sub2"), TemplateFile);
        }
        catch (Exception ex) when (ex is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or System.Xml.XmlException or System.IO.FileFormatException or InvalidCastException)
        {
            throw new InvalidDataException("This isn't a PowerPoint presentation: " + ex.Message, ex);
        }
    }

    private static ThemeLayout WithHints(ThemeLayout layout, List<(string Layout, string? Type, int? Idx, string Text)> samples) =>
        layout with
        {
            Placeholders = [.. layout.Placeholders.Select(p => p with
            {
                Hint = samples.FirstOrDefault(s => s.Layout == layout.PartName && s.Type == p.PhType && s.Idx == p.PhIdx).Text,
            })],
        };

    private static List<(string Layout, string? Type, int? Idx, string Text)> SampleTexts(PresentationPart presentation)
    {
        var found = new List<(string, string?, int?, string)>();
        foreach (var slide in presentation.SlideParts)
        {
            var layout = slide.SlideLayoutPart?.Uri.OriginalString;
            if (layout is null)
                continue;
            foreach (var sp in Load(slide).Descendants(P + "sp"))
            {
                if (sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph") is not { } ph)
                    continue;
                var text = string.Join(" ", sp.Descendants(A + "t").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
                if (text.Length > 0)
                    found.Add((layout, (string?)ph.Attribute("type"), (int?)ph.Attribute("idx"), text.Length > 80 ? text[..80] : text));
            }
        }

        return found;
    }

    /// <summary>The text of the template's own sample slides, by the layout each uses (what it says is often how the template is meant to be used).</summary>
    public static IReadOnlyList<(string Layout, string Text)> SampleSlides(string templatePath)
    {
        using var document = PresentationDocument.Open(templatePath, false);
        var slides = new List<(string, string)>();
        foreach (var slide in document.PresentationPart?.SlideParts ?? [])
        {
            var name = (string?)Load(slide.SlideLayoutPart!).Root!.Element(P + "cSld")?.Attribute("name") ?? string.Empty;
            var text = string.Join(" | ", Load(slide).Descendants(A + "t").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
            if (text.Length > 0)
                slides.Add((name, text.Length > 600 ? text[..600] : text));
        }

        return slides;
    }

    /// <summary>The layout without the roles it doesn't use (a title layout has no body, a content layout no lines under the title).</summary>
    private static ThemeLayout Prune(ThemeLayout layout, params string[] roles) =>
        layout with { Placeholders = [.. layout.Placeholders.Where(p => !roles.Contains(p.Role))] };

    private sealed record Described(string Name, string? Type, ThemeLayout Layout, int OtherPlaceholders, bool HasCenterTitle);

    private static bool NameHas(string name, params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static Described PickTitle(List<Described> layouts) =>
        layouts.FirstOrDefault(l => l.Type == "title" || l.HasCenterTitle) ??
        layouts.FirstOrDefault(l => NameHas(l.Name, "title slide", "標題投影片", "封面")) ??
        layouts.FirstOrDefault(l => l.OtherPlaceholders >= 2) ??
        layouts[0];

    private static Described PickContent(List<Described> layouts, Described title) =>
        layouts.FirstOrDefault(l => l != title && (l.Type == "obj" || NameHas(l.Name, "title and content", "標題及物件", "標題及內容"))) ??
        layouts.Where(l => l != title && l.Layout.Role("body") is not null).OrderBy(l => l.OtherPlaceholders).FirstOrDefault() ??
        layouts.FirstOrDefault(l => l.Layout.Role("body") is not null) ??
        title;

    private static Described Describe(SlideLayoutPart part, XDocument master, Palette palette, List<StaticShape> masterShapes, string folder)
    {
        var xml = Load(part);
        var root = xml.Root!;
        var name = (string?)root.Element(P + "cSld")?.Attribute("name") ?? string.Empty;
        var type = (string?)root.Attribute("type");
        var showMaster = (string?)root.Attribute("showMasterSp") != "0";

        var shapes = new List<StaticShape>();
        if (showMaster)
            shapes.AddRange(masterShapes);
        shapes.AddRange(Shapes(xml, part, folder, palette));

        // Placeholders of the layout that carry text on a slide (not date, footer, page number or pictures).
        var found = new List<(string? Type, int? Idx, Frame Frame, TextLook Look)>();
        foreach (var sp in root.Descendants(P + "sp"))
        {
            if (sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph") is not { } ph)
                continue;
            var phType = (string?)ph.Attribute("type");
            if (phType is "dt" or "ftr" or "sldNum" or "pic" or "chart" or "tbl" or "media")
                continue;
            var phIdx = (int?)ph.Attribute("idx");
            var masterSp = MasterPlaceholder(master, phType);
            var frame = FrameOf(sp) ?? (masterSp is null ? null : FrameOf(masterSp));
            if (frame is null)
                continue;
            found.Add((phType, phIdx, frame, LookOf(sp, masterSp, phType is "title" or "ctrTitle", master, palette)));
        }

        var placeholders = new List<ThemePlaceholder>();
        var titleFound = found.FirstOrDefault(f => f.Type is "title" or "ctrTitle");
        if (titleFound.Frame is not null)
            placeholders.Add(new ThemePlaceholder("title", titleFound.Type, titleFound.Idx, titleFound.Frame, titleFound.Look));
        var others = found.Where(f => f.Type is not ("title" or "ctrTitle")).ToList();

        // A layout with one big text area is a content layout; one with several smaller ones is a title layout (lines under the title).
        var biggest = others.OrderByDescending(o => o.Frame.W * o.Frame.H).FirstOrDefault();
        if (biggest.Frame is not null)
            placeholders.Add(new ThemePlaceholder("body", biggest.Type, biggest.Idx, biggest.Frame, biggest.Look));
        var lines = others.OrderBy(o => o.Frame.Y).ThenBy(o => o.Frame.X).Take(2).ToList();
        for (var i = 0; i < lines.Count; i++)
            placeholders.Add(new ThemePlaceholder($"sub{i + 1}", lines[i].Type, lines[i].Idx, lines[i].Frame, lines[i].Look));

        var layout = new ThemeLayout(name, part.Uri.OriginalString, shapes, placeholders);
        return new Described(name, type, layout, others.Count, found.Any(f => f.Type == "ctrTitle"));
    }

    private static XElement? MasterPlaceholder(XDocument master, string? type)
    {
        var wanted = type is "title" or "ctrTitle" ? "title" : "body";
        return master.Root!.Descendants(P + "sp").FirstOrDefault(sp =>
            sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph") is { } ph && ((string?)ph.Attribute("type") ?? "body") == wanted);
    }

    #region Static shapes

    private static IEnumerable<StaticShape> Shapes(XDocument xml, OpenXmlPart owner, string folder, Palette palette)
    {
        var tree = xml.Root!.Element(P + "cSld")?.Element(P + "spTree");
        if (tree is null)
            yield break;

        foreach (var element in tree.Elements())
        {
            if (element.Name == P + "pic")
            {
                var blip = element.Descendants(A + "blip").FirstOrDefault();
                var rid = (string?)blip?.Attribute(R + "embed");
                if (rid is null || FrameOf(element) is not { } frame || owner.GetPartById(rid) is not ImagePart image)
                    continue;
                var file = SaveImage(image, folder);
                yield return new StaticShape("image", frame, Image: file);
            }
            else if (element.Name == P + "cxnSp" || element.Name == P + "sp")
            {
                if (element.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph") is not null || FrameOf(element) is not { } frame)
                    continue;

                if (element.Descendants(A + "fld").Any(f => (string?)f.Attribute("type") == "slidenum"))
                {
                    yield return new StaticShape("pageNumber", frame, Text: NumberLook(element, palette));
                    continue;
                }

                var hasText = element.Descendants(A + "t").Any(t => t.Value.Trim().Length > 0);
                var line = element.Element(P + "spPr")?.Element(A + "ln");
                var lineColor = line is null ? null : palette.Color(line);
                var fill = element.Element(P + "spPr")?.Element(A + "solidFill") is { } solid ? palette.ColorOf(solid) : null;
                var geometry = (string?)element.Element(P + "spPr")?.Element(A + "prstGeom")?.Attribute("prst");

                if (!hasText && lineColor is not null && (geometry is "line" or "straightConnector1" || frame.W < 0.5 || frame.H < 0.5))
                    yield return new StaticShape("line", frame, Color: lineColor, Weight: (double?)line!.Attribute("w") / EmuPerPoint ?? 0.75);
                else if (!hasText && fill is not null)
                    yield return new StaticShape("rect", frame, Color: fill);
            }
        }
    }

    private static string SaveImage(ImagePart image, string folder)
    {
        using var stream = image.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var extension = image.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            "image/x-emf" => ".emf",
            "image/x-wmf" => ".wmf",
            _ => System.Text.RegularExpressions.Regex.Replace(Path.GetExtension(image.Uri.OriginalString).ToLowerInvariant(), "[^a-z0-9.]", string.Empty),
        };
        var file = "image-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..10].ToLowerInvariant() + extension;
        File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return file;
    }

    private static TextLook NumberLook(XElement shape, Palette palette)
    {
        var run = shape.Descendants(A + "fld").Select(f => f.Element(A + "rPr")).FirstOrDefault(r => r is not null)
                  ?? shape.Descendants(A + "endParaRPr").FirstOrDefault();
        var size = (double?)run?.Attribute("sz") / 100 ?? 10;
        var color = run is null ? null : palette.Color(run) ?? palette.ColorOf(run.Element(A + "solidFill"));
        var align = (string?)shape.Descendants(A + "pPr").FirstOrDefault()?.Attribute("algn");
        return new TextLook(size, (string?)run?.Attribute("b") is "1" or "true", color ?? palette.Ink, Align(align), "middle", palette.Font(run));
    }

    #endregion

    #region Text looks

    private static Frame? FrameOf(XElement shape)
    {
        var xfrm = shape.Element(P + "spPr")?.Element(A + "xfrm") ?? shape.Element(P + "xfrm") ?? shape.Descendants(A + "xfrm").FirstOrDefault();
        var off = xfrm?.Element(A + "off");
        var ext = xfrm?.Element(A + "ext");
        if (off is null || ext is null)
            return null;
        return new Frame(
            (double)off.Attribute("x")! / EmuPerPoint, (double)off.Attribute("y")! / EmuPerPoint,
            (double)ext.Attribute("cx")! / EmuPerPoint, (double)ext.Attribute("cy")! / EmuPerPoint);
    }

    /// <summary>What the first level of a placeholder looks like: the layout's own style, else the master placeholder's, else the master's text style.</summary>
    private static TextLook LookOf(XElement layoutSp, XElement? masterSp, bool isTitle, XDocument master, Palette palette)
    {
        var styles = master.Root!.Element(P + "txStyles");
        var chain = new List<XElement?>
        {
            layoutSp.Element(P + "txBody")?.Element(A + "lstStyle")?.Element(A + "lvl1pPr"),
            masterSp?.Element(P + "txBody")?.Element(A + "lstStyle")?.Element(A + "lvl1pPr"),
            styles?.Element(P + (isTitle ? "titleStyle" : "bodyStyle"))?.Element(A + "lvl1pPr"),
        };

        XElement? Run(Func<XElement, bool> has) => chain.Select(c => c?.Element(A + "defRPr")).FirstOrDefault(r => r is not null && has(r));
        var sizeRun = Run(r => r.Attribute("sz") is not null);
        var boldRun = Run(r => r.Attribute("b") is not null);
        var colorRun = Run(r => r.Element(A + "solidFill") is not null);
        var fontRun = Run(r => r.Element(A + "latin") is not null || r.Element(A + "ea") is not null);
        // A font the layout or the master placeholder names itself is the template's brand font; one that only comes from the theme's text styles is not.
        var ownRun = chain.Take(2).Select(c => c?.Element(A + "defRPr")).FirstOrDefault(r => r is not null && (r.Element(A + "latin") is not null || r.Element(A + "ea") is not null));
        var align = chain.Select(c => (string?)c?.Attribute("algn")).FirstOrDefault(a => a is not null);
        var anchor = (string?)layoutSp.Element(P + "txBody")?.Element(A + "bodyPr")?.Attribute("anchor")
                     ?? (string?)masterSp?.Element(P + "txBody")?.Element(A + "bodyPr")?.Attribute("anchor");

        return new TextLook(
            (double?)sizeRun?.Attribute("sz") / 100 ?? (isTitle ? 32 : 18),
            (string?)boldRun?.Attribute("b") is "1" or "true",
            colorRun is null ? palette.Ink : palette.ColorOf(colorRun.Element(A + "solidFill")) ?? palette.Ink,
            Align(align),
            anchor switch { "ctr" => "middle", "b" => "bottom", _ => "top" },
            palette.Font(fontRun),
            palette.Face(ownRun, "latin"), palette.Face(ownRun, "ea"));
    }

    private static string Align(string? algn) => algn switch { "ctr" => "center", "r" => "right", _ => "left" };

    #endregion

    private static XDocument Load(OpenXmlPart part)
    {
        using var stream = part.GetStream();
        return XDocument.Load(stream);
    }

    /// <summary>The template's colours (through its colour map) and fonts.</summary>
    private sealed class Palette
    {
        private readonly Dictionary<string, string> _scheme = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
        private string _majorLatin = "Calibri", _majorEa = "Microsoft JhengHei", _minorLatin = "Calibri", _minorEa = "Microsoft JhengHei";

        public string Ink => ColorOf("tx1") ?? "000000";

        public static Palette From(SlideMasterPart master, XDocument masterXml)
        {
            var palette = new Palette();
            foreach (var attribute in masterXml.Root!.Element(P + "clrMap")?.Attributes() ?? [])
                palette._map[attribute.Name.LocalName] = attribute.Value;

            if (master.ThemePart is { } themePart)
            {
                using var stream = themePart.GetStream();
                var theme = XDocument.Load(stream);
                foreach (var color in theme.Descendants(A + "clrScheme").Elements().Elements())
                {
                    var key = color.Parent!.Name.LocalName;
                    palette._scheme[key] = (string?)color.Attribute("val") is { Length: 6 } rgb ? rgb : ((string?)color.Attribute("lastClr") ?? "000000");
                }

                string Face(string group, string kind) => (string?)theme.Descendants(A + group).Elements(A + kind).FirstOrDefault()?.Attribute("typeface") ?? string.Empty;
                palette._majorLatin = Face("majorFont", "latin") is { Length: > 0 } ml ? ml : palette._majorLatin;
                palette._minorLatin = Face("minorFont", "latin") is { Length: > 0 } nl ? nl : palette._minorLatin;
                palette._majorEa = Face("majorFont", "ea") is { Length: > 0 } me ? me : palette._majorEa;
                palette._minorEa = Face("minorFont", "ea") is { Length: > 0 } ne ? ne : palette._minorEa;
            }

            return palette;
        }

        private string? ColorOf(string schemeName)
        {
            var mapped = _map.GetValueOrDefault(schemeName, schemeName);
            return _scheme.GetValueOrDefault(mapped);
        }

        /// <summary>The colour of a line or run properties element: its solid fill (a line keeps it in <c>a:ln</c>).</summary>
        public string? Color(XElement owner) => ColorOf(owner.Element(A + "solidFill"));

        public string? ColorOf(XElement? solidFill)
        {
            var color = solidFill?.Elements().FirstOrDefault();
            return color?.Name.LocalName switch
            {
                "srgbClr" => Hex((string?)color.Attribute("val")),
                "schemeClr" => ColorOf((string?)color.Attribute("val") ?? "tx1"),
                "sysClr" => (string?)color.Attribute("lastClr") ?? "000000",
                "prstClr" => (string?)color.Attribute("val") == "white" ? "FFFFFF" : "000000",
                _ => null,
            };
        }

        /// <summary>The typeface a run's properties name for <paramref name="kind"/> (latin or ea), theme fonts resolved; null when it names none.</summary>
        public string? Face(XElement? run, string kind)
        {
            var face = (string?)run?.Element(A + kind)?.Attribute("typeface");
            return Safe(face switch
            {
                null or "" => null,
                "+mj-lt" => _majorLatin,
                "+mn-lt" => _minorLatin,
                "+mj-ea" => _majorEa,
                "+mn-ea" => _minorEa,
                var other => other,
            });
        }

        /// <summary>A colour as RRGGBB, or null when it is anything else (what a template says ends up in the preview page).</summary>
        private static string? Hex(string? value) => value is { Length: 6 } && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;

        /// <summary>A font name with only letters, digits, spaces and a few marks, so it can't break out of the style it is written into.</summary>
        private static string? Safe(string? face)
        {
            var cleaned = face is null ? null : System.Text.RegularExpressions.Regex.Replace(face, @"[^\p{L}\p{N} _.\-]", string.Empty).Trim();
            return string.IsNullOrEmpty(cleaned) ? null : cleaned;
        }

        /// <summary>A CSS font list from the Latin and the East Asian font of a run's properties (theme fonts resolved).</summary>
        public string Font(XElement? run)
        {
            string? Resolve(string? face, bool ea) => face switch
            {
                null or "" => null,
                "+mj-lt" => _majorLatin,
                "+mn-lt" => _minorLatin,
                "+mj-ea" => _majorEa,
                "+mn-ea" => _minorEa,
                var other => other,
            };

            var ea = Resolve((string?)run?.Element(A + "ea")?.Attribute("typeface"), true);
            var latin = Resolve((string?)run?.Element(A + "latin")?.Attribute("typeface"), false);
            var faces = new[] { latin, ea, _minorLatin, _minorEa }.Select(Safe).Where(f => !string.IsNullOrEmpty(f)).Distinct().Select(f => $"'{f!.Replace("'", string.Empty, StringComparison.Ordinal)}'");
            return string.Join(',', faces) + ",sans-serif";
        }
    }
}
