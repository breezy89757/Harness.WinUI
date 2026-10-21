// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Harness.Core.Artifacts;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Harness.Core.Files;

/// <summary>
/// Creates a real PowerPoint file (no Office needed) from a <see cref="SlideDeck"/>. With a company theme (see <see cref="BrandTheme"/>)
/// the file is made on the company's own template: its slide master, its layouts, its logos, lines and fonts, with the slides of the
/// deck added using the template's title, content and section layouts. Without one, a plain master with three layouts is made. Either
/// way the slides use the layouts' placeholders, so titles show in PowerPoint's outline, lists are real bullet lists, tables are real
/// tables and the layouts can be changed afterwards. Speaker notes go to the notes pages.
/// </summary>
public static class PowerPointWriter
{
    /// <summary>The colour of the rule under titles, of the bullets and of table headers in the built-in theme.</summary>
    public const string Accent = "1F4E79";

    private const string Ink = "1F2937";
    private const string Muted = "6B7280";
    private const string Rule = "D1D5DB";

    // The geometry of the built-in master (points), the same as BrandTheme.BuiltIn describes.
    private const double Margin = 48;
    private const double SlideWidth = 960;
    private const double SlideHeight = 540;
    private const double ContentTop = 112;
    private const double ContentWidth = 864;
    private const double ContentHeight = 380;

    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private const string GroupHeader =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    /// <param name="theme">The company theme; null or the built-in one makes a deck on a plain master.</param>
    /// <param name="templatePath">The theme's template file (required for a company theme).</param>
    public static void WritePresentation(string path, SlideDeck deck, string? title = null, BrandTheme? theme = null, string? templatePath = null)
    {
        ArgumentNullException.ThrowIfNull(deck);
        theme ??= BrandTheme.BuiltIn;
        if (theme.IsBuiltIn)
        {
            WriteBuiltIn(path, deck, title, theme);
            return;
        }

        if (templatePath is null || !File.Exists(templatePath))
            throw new FileNotFoundException("The template of the theme is missing.", templatePath);
        WriteOnTemplate(path, deck, title, theme, templatePath);
    }

    #region Built-in master

    private static void WriteBuiltIn(string path, SlideDeck deck, string? title, BrandTheme theme)
    {
        using var document = PresentationDocument.Create(path, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
        document.PackageProperties.Title = title ?? deck.Slides.FirstOrDefault()?.Title;

        var presentationPart = document.AddPresentationPart();
        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        masterPart.AddNewPart<ThemePart>().Theme = new A.Theme(ThemeXml("Harness"));

        var titleLayout = AddLayout(masterPart, TitleLayoutXml());
        var contentLayout = AddLayout(masterPart, ContentLayoutXml());
        var sectionLayout = AddLayout(masterPart, SectionLayoutXml());
        masterPart.SlideMaster = new P.SlideMaster(MasterXml(
            masterPart.GetIdOfPart(titleLayout), masterPart.GetIdOfPart(contentLayout), masterPart.GetIdOfPart(sectionLayout)));

        var notesMaster = AddNotesMaster(presentationPart);
        var slideIds = AddSlides(presentationPart, deck, theme, kind => kind switch
        {
            SlideKind.Title => titleLayout,
            SlideKind.Section => sectionLayout,
            _ => contentLayout,
        }, notesMaster, includeSlideNumber: true);

        presentationPart.Presentation = new P.Presentation(
            $"<p:presentation {Namespaces} saveSubsetFonts=\"1\">" +
            $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{presentationPart.GetIdOfPart(masterPart)}\"/></p:sldMasterIdLst>" +
            $"<p:notesMasterIdLst><p:notesMasterId r:id=\"{presentationPart.GetIdOfPart(notesMaster)}\"/></p:notesMasterIdLst>" +
            $"<p:sldIdLst>{string.Concat(slideIds.Select(s => $"<p:sldId id=\"{s.Id}\" r:id=\"{s.Relationship}\"/>"))}</p:sldIdLst>" +
            $"<p:sldSz cx=\"{Emu(SlideWidth)}\" cy=\"{Emu(SlideHeight)}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "</p:presentation>");
    }

    private static SlideLayoutPart AddLayout(SlideMasterPart master, string xml)
    {
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.AddPart(master);
        layout.SlideLayout = new P.SlideLayout(xml);
        return layout;
    }

    #endregion

    #region On a company template

    private static void WriteOnTemplate(string path, SlideDeck deck, string? title, BrandTheme theme, string templatePath)
    {
        File.Copy(templatePath, path, overwrite: true);
        using var document = PresentationDocument.Open(path, true);
        var presentationPart = document.PresentationPart ?? throw new InvalidDataException("The template has no presentation.");
        var presentation = presentationPart.Presentation ?? throw new InvalidDataException("The template has no presentation.");

        // The template's own sample slides go; its master and layouts stay.
        foreach (var old in presentationPart.SlideParts.ToList())
            presentationPart.DeletePart(old);
        presentation.SlideIdList?.RemoveAllChildren();
        presentation.CustomShowList?.Remove();
        presentation.PresentationExtensionList?.Remove(); // sections that name the old slides
        if (document.ExtendedFilePropertiesPart is { } properties)
            document.DeletePart(properties); // its list of slide titles is the old one

        var layouts = presentationPart.SlideMasterParts.SelectMany(m => m.SlideLayoutParts).ToList();
        SlideLayoutPart Layout(ThemeLayout layout) =>
            layouts.FirstOrDefault(l => string.Equals(l.Uri.OriginalString, layout.PartName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The template has no layout '{layout.Name}'.");

        var titleLayout = Layout(theme.Title);
        var contentLayout = Layout(theme.Content);
        var sectionLayout = theme.Section is { } section ? Layout(section) : titleLayout;

        var hadNotesMaster = presentationPart.NotesMasterPart is not null;
        var notesMaster = presentationPart.NotesMasterPart ?? AddNotesMaster(presentationPart);
        var slideIds = AddSlides(presentationPart, deck, theme, kind => kind switch
        {
            SlideKind.Title => titleLayout,
            SlideKind.Section => sectionLayout,
            _ => contentLayout,
        }, notesMaster, includeSlideNumber: false);

        if (!hadNotesMaster)
        {
            var notesIds = new P.NotesMasterIdList(new P.NotesMasterId { Id = presentationPart.GetIdOfPart(notesMaster) });
            presentation.InsertAfter(notesIds, presentation.SlideMasterIdList);
        }

        var list = new P.SlideIdList();
        foreach (var (id, relationship) in slideIds)
            list.Append(new P.SlideId { Id = id, RelationshipId = relationship });
        if (presentation.SlideIdList is { } current)
            current.Remove();
        presentation.InsertAfter(list, (DocumentFormat.OpenXml.OpenXmlElement?)presentation.HandoutMasterIdList ?? (DocumentFormat.OpenXml.OpenXmlElement?)presentation.NotesMasterIdList ?? presentation.SlideMasterIdList);

        document.PackageProperties.Title = title ?? deck.Slides.FirstOrDefault()?.Title;
    }

    #endregion

    #region Slides

    private static NotesMasterPart AddNotesMaster(PresentationPart presentationPart)
    {
        var notesMaster = presentationPart.AddNewPart<NotesMasterPart>();
        notesMaster.AddNewPart<ThemePart>().Theme = new A.Theme(ThemeXml("Harness notes"));
        notesMaster.NotesMaster = new P.NotesMaster(NotesMasterXml());
        return notesMaster;
    }

    /// <returns>The ids of the slides added, with the relationship each is reached by.</returns>
    private static List<(uint Id, string Relationship)> AddSlides(
        PresentationPart presentationPart, SlideDeck deck, BrandTheme theme, Func<SlideKind, SlideLayoutPart> layoutFor, NotesMasterPart notesMaster, bool includeSlideNumber)
    {
        var metrics = new SlideLayout(theme);
        var ids = new List<(uint, string)>();
        var id = 256u;
        foreach (var slide in deck.Slides)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(layoutFor(slide.Kind));
            slidePart.Slide = new P.Slide(SlideXml(slide, theme, metrics, includeSlideNumber, chart => ChartWriter.AddChart(slidePart, chart, theme.Palette, theme.Ink, theme.BodyFonts)));

            if (slide.Notes is { } notes)
            {
                var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                notesPart.AddPart(notesMaster);
                notesPart.AddPart(slidePart);
                notesPart.NotesSlide = new P.NotesSlide(NotesXml(notes));
            }

            ids.Add((id++, presentationPart.GetIdOfPart(slidePart)));
        }

        return ids;
    }

    private static long Emu(double points) => (long)Math.Round(points * 12700);

    private static string Xfrm(double x, double y, double width, double height, string tag = "a:xfrm") =>
        $"<{tag}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(width)}\" cy=\"{Emu(height)}\"/></{tag}>";

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static string Fonts(BrandTheme theme) => theme.IsBuiltIn ? string.Empty : FontElements(theme.BodyFonts.Latin, theme.BodyFonts.Ea);

    private static string FontElements(string? latin, string? ea) =>
        (latin is null ? string.Empty : $"<a:latin typeface=\"{Esc(latin)}\"/>") + (ea is null ? string.Empty : $"<a:ea typeface=\"{Esc(ea)}\"/>");

    /// <param name="fonts">Font elements for the runs: the template's brand fonts, empty to inherit.</param>
    private static string Run(TextRun run, double? size, string fonts, string? color = null, bool bold = false)
    {
        var attributes = $"lang=\"zh-TW\" altLang=\"en-US\"{(size is null ? string.Empty : $" sz=\"{(int)Math.Round(size.Value * 100)}\"")}{(run.Bold || bold ? " b=\"1\"" : string.Empty)}{(run.Italic ? " i=\"1\"" : string.Empty)} dirty=\"0\"";
        var fill = color is null ? string.Empty : $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill>";
        var font = run.Code ? "<a:latin typeface=\"Consolas\"/>" : fonts;
        return $"<a:r><a:rPr {attributes}>{fill}{font}</a:rPr><a:t>{Esc(run.Text)}</a:t></a:r>";
    }

    private static string Runs(IEnumerable<TextRun> runs, double? size, string fonts, string? color = null, bool bold = false) =>
        string.Concat(runs.Where(r => r.Text.Length > 0).Select(r => Run(r, size, fonts, color, bold)));

    private static string Paragraph(SlideBlock block, double size, string fonts, string? bulletColor) => block switch
    {
        BulletBlock bullet =>
            $"<a:p><a:pPr lvl=\"{bullet.Level}\" marL=\"{Emu(28 * (bullet.Level + 1))}\" indent=\"-{Emu(28)}\">" +
            (bulletColor is null ? string.Empty : $"<a:buClr><a:srgbClr val=\"{bulletColor}\"/></a:buClr>") +
            (bullet.Numbered
                ? "<a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"/>"
                : $"<a:buFont typeface=\"Arial\"/><a:buChar char=\"{(bullet.Level == 0 ? "•" : "–")}\"/>") + "</a:pPr>" +
            $"{Runs(bullet.Runs, bullet.Level == 0 ? size : size * 0.9, fonts)}</a:p>",
        ParagraphBlock paragraph =>
            $"<a:p><a:pPr marL=\"0\" indent=\"0\"><a:spcBef><a:spcPts val=\"900\"/></a:spcBef><a:buNone/></a:pPr>{Runs(paragraph.Runs, size, fonts)}</a:p>",
        _ => string.Empty,
    };

    /// <summary>A shape for one of the layout's text places: its placeholder when the layout has one there, else a plain text box.</summary>
    private static string TextShape(ref int id, string name, ThemePlaceholder place, string paragraphs, string geometry = "")
    {
        var phAttributes = (place.PhType is null ? string.Empty : $" type=\"{place.PhType}\"") + (place.PhIdx is null ? string.Empty : $" idx=\"{place.PhIdx}\"");
        if (place.PhType is null && place.PhIdx is null)
        {
            return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
                   $"<p:spPr>{Xfrm(place.Frame.X, place.Frame.Y, place.Frame.W, place.Frame.H)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
                   $"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
        }

        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph{phAttributes}/></p:nvPr></p:nvSpPr>" +
               $"<p:spPr>{geometry}</p:spPr><p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
    }

    /// <param name="addChart">Puts a chart on the slide's part and says which relationship it is reached by.</param>
    private static string SlideXml(Slide slide, BrandTheme theme, SlideLayout metrics, bool includeSlideNumber, Func<ChartBlock, string> addChart)
    {
        var layout = slide.Kind switch
        {
            SlideKind.Title => theme.Title,
            SlideKind.Section => theme.Section ?? theme.Title,
            _ => theme.Content,
        };
        var fonts = Fonts(theme);
        var shapes = new StringBuilder();
        var id = 2;

        if (layout.Role("title") is { } titlePlace)
        {
            // A title that is too long for its frame is set smaller (a placeholder only shrinks its text once someone edits it).
            var fitted = metrics.TitlePoints(slide);
            var size = fitted < titlePlace.Look.Size ? $" sz=\"{(int)Math.Round(fitted * 100)}\"" : string.Empty;
            var plain = $"<a:p><a:r><a:rPr lang=\"zh-TW\" altLang=\"en-US\"{size} dirty=\"0\"/><a:t>{Esc(slide.Title)}</a:t></a:r></a:p>";
            shapes.Append(TextShape(ref id, "Title", titlePlace, plain));
        }

        if (slide.Kind == SlideKind.Content)
        {
            var bodySize = metrics.BodyPoints(slide);
            var text = slide.Blocks.Where(b => b is not (TableBlock or ChartBlock)).ToList();
            var textHeight = metrics.TextHeight(slide, bodySize);
            var content = theme.ContentFrame;
            if (text.Count > 0 && layout.Role("body") is { } bodyPlace)
            {
                var geometry = slide.Blocks.Any(b => b is TableBlock or ChartBlock)
                    ? Xfrm(content.X, content.Y, metrics.TextWidth(slide), Math.Min(content.H, textHeight + 8))
                    : string.Empty;
                shapes.Append(TextShape(ref id, "Content", bodyPlace, string.Concat(text.Select(b => Paragraph(b, bodySize, fonts, bulletColor: null))), geometry));
            }
            if (slide.Blocks.OfType<TableBlock>().FirstOrDefault() is { } table && table.Rows.Count > 0)
            {
                var top = content.Y + (text.Count > 0 ? Math.Min(content.H, textHeight) + 16 : 0);
                shapes.Append(TableShape(id++, table, metrics.TablePoints(slide), top, metrics.TextWidth(slide), theme));
            }
            if (slide.Blocks.OfType<ChartBlock>().FirstOrDefault() is { } chart && metrics.ChartFrame(slide) is { } chartFrame)
            {
                shapes.Append(
                    $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id++}\" name=\"Chart\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>" +
                    Xfrm(chartFrame.X, chartFrame.Y, chartFrame.W, chartFrame.H, "p:xfrm") +
                    "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
                    $"<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"{addChart(chart)}\"/></a:graphicData></a:graphic></p:graphicFrame>");
            }
            if (includeSlideNumber)
            {
                shapes.Append(
                    $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"Slide Number\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldNum\" sz=\"quarter\" idx=\"12\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
                    $"<p:txBody><a:bodyPr/><a:lstStyle/>{SlideNumberField}</p:txBody></p:sp>");
            }
        }
        else
        {
            // A title slide: the subtitle goes under the title, the lines after it (who, when) below that. A section slide: its subtitle.
            var lines = new List<IReadOnlyList<TextRun>>();
            if (slide.Subtitle is { Length: > 0 } subtitle)
                lines.Add([new TextRun(subtitle)]);
            if (slide.Kind == SlideKind.Title)
            {
                foreach (var block in slide.Blocks)
                    lines.Add(block is BulletBlock b ? b.Runs : block is ParagraphBlock p ? p.Runs : []);
            }

            // A line too long for its place is set smaller; a place of the template only shrinks its text once someone edits it.
            var fitted = metrics.LinePoints(slide);
            string Lines(IEnumerable<IReadOnlyList<TextRun>> runs, TextLook look, bool explicitLook, double fit)
            {
                double? size = explicitLook ? Math.Min(look.Size, fit > 0 ? fit : look.Size) : fit > 0 && fit < look.Size ? fit : null;
                return string.Concat(runs.Select(r => $"<a:p><a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr>{Runs(r, size, explicitLook ? string.Empty : fonts, explicitLook ? look.Color : null)}</a:p>"));
            }

            // A layout with two places for lines gets the subtitle in the first and the rest in the second; with one place, it gets them all.
            var second = layout.Role("sub2");
            if (layout.Role("sub1") is { } first && lines.Count > 0)
                shapes.Append(TextShape(ref id, "Subtitle", first, Lines(second is null ? lines : lines.Take(1), first.Look, first.PhType is null && first.PhIdx is null, fitted.First)));
            if (second is not null && lines.Count > 1)
                shapes.Append(TextShape(ref id, "Details", second, Lines(lines.Skip(1), second.Look, second.PhType is null && second.PhIdx is null, fitted.Second)));
        }

        return $"<p:sld {Namespaces}><p:cSld><p:spTree>{GroupHeader}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string TableShape(int id, TableBlock table, double size, double top, double width, BrandTheme theme)
    {
        var content = theme.ContentFrame;
        var columns = Math.Max(1, table.Rows.Max(r => r.Count));
        var columnWidth = Emu(width / columns);
        var grid = string.Concat(Enumerable.Repeat($"<a:gridCol w=\"{columnWidth}\"/>", columns));
        var inner = width / columns - 14;
        var border = $"<a:solidFill><a:srgbClr val=\"{Rule}\"/></a:solidFill>";
        var fonts = Fonts(theme);
        var headerFill = theme.IsBuiltIn ? "<a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill>" : $"<a:solidFill><a:srgbClr val=\"{theme.Accent}\"/></a:solidFill>";

        var rows = new StringBuilder();
        double height = 0;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var header = table.HasHeader && r == 0;
            var rowHeight = Math.Max(1, row.Select(c => SlideLayout.Lines(SlideDeck.TextOf(c), inner, size)).DefaultIfEmpty(1).Max()) * size * 1.25 + 12;
            height += rowHeight;
            rows.Append(CultureInfo.InvariantCulture, $"<a:tr h=\"{Emu(rowHeight)}\">");
            for (var c = 0; c < columns; c++)
            {
                var cell = c < row.Count ? row[c] : [new TextRun(string.Empty)];
                var content2 = Runs(cell, size, fonts, header ? "FFFFFF" : null, bold: header);
                rows.Append(
                    $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p>{(content2.Length == 0 ? $"<a:endParaRPr lang=\"en-US\" sz=\"{(int)(size * 100)}\"/>" : content2)}</a:p></a:txBody>" +
                    $"<a:tcPr marL=\"{Emu(7)}\" marR=\"{Emu(7)}\" marT=\"{Emu(4)}\" marB=\"{Emu(4)}\" anchor=\"ctr\">" +
                    $"<a:lnL w=\"9525\">{border}</a:lnL><a:lnR w=\"9525\">{border}</a:lnR><a:lnT w=\"9525\">{border}</a:lnT><a:lnB w=\"9525\">{border}</a:lnB>" +
                    (header ? headerFill : "<a:noFill/>") + "</a:tcPr></a:tc>");
            }
            rows.Append("</a:tr>");
        }

        return $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>" +
               Xfrm(content.X, top, width, height, "p:xfrm") +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">" +
               $"<a:tbl><a:tblPr firstRow=\"{(table.HasHeader ? 1 : 0)}\"/><a:tblGrid>{grid}</a:tblGrid>{rows}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
    }

    #endregion

    #region Built-in theme, master and layouts

    private static string ThemeXml(string name) =>
        $"<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"{name}\">" +
        "<a:themeElements>" +
        $"<a:clrScheme name=\"Harness\"><a:dk1><a:srgbClr val=\"{Ink}\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"F3F4F6\"/></a:lt2>" +
        $"<a:accent1><a:srgbClr val=\"{Accent}\"/></a:accent1><a:accent2><a:srgbClr val=\"2E75B6\"/></a:accent2><a:accent3><a:srgbClr val=\"548235\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"BF9000\"/></a:accent4><a:accent5><a:srgbClr val=\"C55A11\"/></a:accent5><a:accent6><a:srgbClr val=\"7030A0\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Harness\">" +
        "<a:majorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"Microsoft JhengHei\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"Microsoft JhengHei\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Harness\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements></a:theme>";

    private static string Placeholder(int id, string name, string ph, double x, double y, double width, double height, string bodyPr, string body, bool geometry = true) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{(geometry ? Xfrm(x, y, width, height) : string.Empty)}</p:spPr>" +
        $"<p:txBody>{bodyPr}<a:lstStyle/>{body}</p:txBody></p:sp>";

    private static string RuleShape(int id, double x, double y, double width, double height) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Accent rule\"/><p:cNvSpPr/><p:nvPr userDrawn=\"1\"/></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, width, height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";

    private const string EditTitle = "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Click to edit the title</a:t></a:r></a:p>";
    private const string EditText = "<a:p><a:pPr lvl=\"0\"/><a:r><a:rPr lang=\"en-US\"/><a:t>Click to edit the text</a:t></a:r></a:p>";

    private static readonly string SlideNumberField =
        "<a:p><a:fld id=\"{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}\" type=\"slidenum\"><a:rPr lang=\"en-US\" sz=\"1200\"><a:solidFill><a:srgbClr val=\"" + Muted + "\"/></a:solidFill></a:rPr><a:t>‹#›</a:t></a:fld><a:endParaRPr lang=\"en-US\"/></a:p>";

    private static string SlideNumberPlaceholder(int id) =>
        Placeholder(id, "Slide Number", "<p:ph type=\"sldNum\" sz=\"quarter\" idx=\"12\"/>", SlideWidth - Margin - 72, 500, 72, 24,
            "<a:bodyPr/>", SlideNumberField);

    private static string MasterXml(string titleLayoutId, string contentLayoutId, string sectionLayoutId)
    {
        var level = new StringBuilder();
        for (var i = 1; i <= 5; i++)
        {
            var marL = Emu(28 * i);
            level.Append(CultureInfo.InvariantCulture,
                $"<a:lvl{i}pPr marL=\"{marL}\" indent=\"-{Emu(28)}\" algn=\"l\"><a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"{(i == 1 ? 900 : 500)}\"/></a:spcBef>" +
                $"<a:buClr><a:schemeClr val=\"accent1\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{(i == 1 ? "•" : "–")}\"/>" +
                $"<a:defRPr sz=\"{(i == 1 ? 2400 : 2000)}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl{i}pPr>");
        }

        return $"<p:sldMaster {Namespaces}><p:cSld><p:bg><p:bgPr><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{GroupHeader}" +
               Placeholder(2, "Title Placeholder", "<p:ph type=\"title\"/>", Margin, 24, ContentWidth, 72,
                   "<a:bodyPr vert=\"horz\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"45720\" rtlCol=\"0\" anchor=\"b\"><a:normAutofit/></a:bodyPr>", EditTitle) +
               Placeholder(3, "Text Placeholder", "<p:ph type=\"body\" idx=\"1\"/>", Margin, ContentTop, ContentWidth, ContentHeight,
                   "<a:bodyPr vert=\"horz\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" rtlCol=\"0\"><a:normAutofit/></a:bodyPr>", EditText) +
               SlideNumberPlaceholder(4) +
               "</p:spTree></p:cSld>" +
               "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
               $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{titleLayoutId}\"/><p:sldLayoutId id=\"2147483650\" r:id=\"{contentLayoutId}\"/><p:sldLayoutId id=\"2147483651\" r:id=\"{sectionLayoutId}\"/></p:sldLayoutIdLst>" +
               "<p:txStyles>" +
               "<p:titleStyle><a:lvl1pPr algn=\"l\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/>" +
               "<a:defRPr sz=\"3200\" b=\"1\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
               $"<p:bodyStyle>{level}</p:bodyStyle>" +
               "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr><a:lvl1pPr marL=\"0\" algn=\"l\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:otherStyle>" +
               "</p:txStyles></p:sldMaster>";
    }

    private const string TitleBodyPr = "<a:bodyPr anchor=\"b\"><a:normAutofit/></a:bodyPr>";

    private static string TitleLayoutXml() =>
        $"<p:sldLayout {Namespaces} type=\"title\" preserve=\"1\"><p:cSld name=\"Title Slide\"><p:spTree>{GroupHeader}" +
        Placeholder(2, "Title", "<p:ph type=\"ctrTitle\"/>", 72, 150, 816, 120, TitleBodyPr, EditTitle.Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\" sz=\"4400\"/>")) +
        RuleShape(3, 72, 282, 120, 4) +
        Placeholder(4, "Subtitle", "<p:ph type=\"subTitle\" idx=\"1\"/>", 72, 298, 816, 60, "<a:bodyPr><a:normAutofit/></a:bodyPr>",
            $"<a:p><a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"2400\"><a:solidFill><a:srgbClr val=\"{Muted}\"/></a:solidFill></a:rPr><a:t>Click to edit the subtitle</a:t></a:r></a:p>") +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string ContentLayoutXml() =>
        $"<p:sldLayout {Namespaces} type=\"obj\" preserve=\"1\"><p:cSld name=\"Title and Content\"><p:spTree>{GroupHeader}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{EditTitle}</p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{EditText}</p:txBody></p:sp>" +
        RuleShape(4, Margin, 100, ContentWidth, 3) +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Slide Number\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldNum\" sz=\"quarter\" idx=\"12\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{SlideNumberField}</p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string SectionLayoutXml() =>
        $"<p:sldLayout {Namespaces} type=\"secHead\" preserve=\"1\"><p:cSld name=\"Section Header\"><p:spTree>{GroupHeader}" +
        Placeholder(2, "Title", "<p:ph type=\"title\"/>", 72, 200, 816, 100, TitleBodyPr, EditTitle.Replace("<a:rPr lang=\"en-US\"/>", "<a:rPr lang=\"en-US\" sz=\"4000\"/>")) +
        RuleShape(3, 72, 312, 120, 4) +
        Placeholder(4, "Text", "<p:ph type=\"body\" idx=\"1\"/>", 72, 326, 816, 60, "<a:bodyPr><a:normAutofit/></a:bodyPr>",
            $"<a:p><a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"2200\"><a:solidFill><a:srgbClr val=\"{Muted}\"/></a:solidFill></a:rPr><a:t>Click to edit the text</a:t></a:r></a:p>") +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    #endregion

    #region Notes

    private static string NotesMasterXml() =>
        $"<p:notesMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GroupHeader}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"1143000\"/><a:ext cx=\"5486400\" cy=\"3086100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"3\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"4400550\"/><a:ext cx=\"5486400\" cy=\"3600450\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
        "<p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"/><a:lstStyle/><a:p><a:pPr lvl=\"0\"/><a:r><a:rPr lang=\"en-US\"/><a:t>Click to edit the notes</a:t></a:r></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:notesStyle><a:lvl1pPr marL=\"0\" algn=\"l\"><a:defRPr sz=\"1200\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:notesStyle></p:notesMaster>";

    private static string NotesXml(string notes) =>
        $"<p:notes {Namespaces}><p:cSld><p:spTree>{GroupHeader}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{string.Concat(notes.Split('\n').Select(line => $"<a:p><a:r><a:rPr lang=\"zh-TW\" altLang=\"en-US\" dirty=\"0\"/><a:t>{Esc(line.TrimEnd())}</a:t></a:r></a:p>"))}</p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";

    #endregion
}
