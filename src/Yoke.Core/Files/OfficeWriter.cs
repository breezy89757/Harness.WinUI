// Yoke — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using S = DocumentFormat.OpenXml.Spreadsheet;
using TableColumnAlign = Markdig.Extensions.Tables.TableColumnAlign;
using MdCell = Markdig.Extensions.Tables.TableCell;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdTable = Markdig.Extensions.Tables.Table;
using WordTable = DocumentFormat.OpenXml.Wordprocessing.Table;

namespace Yoke.Core.Files;

/// <summary>
/// Creates real Office files with the Open XML SDK (no Office installation needed): Word documents
/// from Markdown — built-in heading styles, native bullet/number lists, tables — and Excel workbooks
/// from tabular data, with numeric cells stored as numbers.
/// </summary>
public static class OfficeWriter
{
    private static readonly MarkdownPipeline s_markdown = new MarkdownPipelineBuilder()
        .UsePipeTables().UseGridTables().UseEmphasisExtras().UseTaskLists().UseAutoLinks().Build();

    private const string BodyFont = "Calibri";
    private const string EastAsiaFont = "Microsoft JhengHei";
    private const string CodeFont = "Consolas";

    #region Word

    public static void WriteWordDocument(string path, string markdown)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddWordStyles(main);
        var numbering = new WordNumbering(main);

        var body = main.Document.Body!;
        foreach (var block in Markdown.Parse(markdown, s_markdown))
            AppendBlock(body, block, main, numbering, listLevel: 0);

        // A4 with 2.54 cm margins.
        body.Append(new SectionProperties(
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 720U, Footer = 720U, Gutter = 0U }));
        main.Document.Save();
    }

    private static void AppendBlock(OpenXmlCompositeElement parent, Block block, MainDocumentPart main, WordNumbering numbering, int listLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.Append(StyledParagraph($"Heading{Math.Clamp(heading.Level, 1, 4)}", InlineRuns(heading.Inline, main)));
                break;

            case ParagraphBlock paragraph:
                parent.Append(new Paragraph(InlineRuns(paragraph.Inline, main)));
                break;

            case ListBlock list:
                var numberingId = list.IsOrdered ? numbering.NewNumberedList() : WordNumbering.BulletId;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    foreach (var child in item)
                    {
                        if (child is ParagraphBlock itemParagraph)
                        {
                            var p = new Paragraph(InlineRuns(itemParagraph.Inline, main));
                            p.PrependChild(new ParagraphProperties(
                                new ParagraphStyleId { Val = "ListParagraph" },
                                new NumberingProperties(
                                    new NumberingLevelReference { Val = Math.Min(listLevel, 2) },
                                    new NumberingId { Val = numberingId })));
                            parent.Append(p);
                        }
                        else
                        {
                            AppendBlock(parent, child, main, numbering, child is ListBlock ? listLevel + 1 : listLevel);
                        }
                    }
                }
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    if (child is ParagraphBlock quoted)
                        parent.Append(StyledParagraph("Quote", InlineRuns(quoted.Inline, main)));
                    else
                        AppendBlock(parent, child, main, numbering, listLevel);
                }
                break;

            case CodeBlock code:
                foreach (var line in code.Lines.Lines.Take(code.Lines.Count))
                    parent.Append(StyledParagraph("Code", [TextRun(line.ToString())]));
                break;

            case MdTable table:
                parent.Append(WordTableFrom(table, main));
                parent.Append(new Paragraph()); // Word needs a paragraph between consecutive tables
                break;

            case ThematicBreakBlock:
                parent.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
                    new BottomBorder { Val = BorderValues.Single, Size = 6U, Color = "BFBFBF", Space = 1U }))));
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    AppendBlock(parent, child, main, numbering, listLevel);
                break;
        }
    }

    private static WordTable WordTableFrom(MdTable table, MainDocumentPart main)
    {
        var border = new Func<BorderType, BorderType>(b => { b.Val = BorderValues.Single; b.Size = 4U; b.Color = "BFBFBF"; return b; });
        var result = new WordTable(new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(
                border(new TopBorder()), border(new LeftBorder()), border(new BottomBorder()), border(new RightBorder()),
                border(new InsideHorizontalBorder()), border(new InsideVerticalBorder())),
            new TableCellMarginDefault(
                new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa })));

        // Word requires a grid; split the A4 text width (11906 - 2 × 1440 twips) evenly.
        var columns = Math.Max(1, table.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(1).Max());
        var grid = new TableGrid();
        for (var c = 0; c < columns; c++)
            grid.Append(new GridColumn { Width = (9026 / columns).ToString(CultureInfo.InvariantCulture) });
        result.Append(grid);

        foreach (var row in table.OfType<MdRow>())
        {
            var wordRow = new TableRow();
            if (row.IsHeader)
                wordRow.Append(new TableRowProperties(new TableHeader()));

            foreach (var (cell, column) in row.OfType<MdCell>().Select((cell, column) => (cell, column)))
            {
                var runs = cell.OfType<ParagraphBlock>().SelectMany(p => InlineRuns(p.Inline, main, bold: row.IsHeader)).ToList();
                var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
                var paragraph = new Paragraph(new ParagraphProperties(
                    new SpacingBetweenLines { After = "0" },
                    new Justification
                    {
                        Val = alignment switch
                        {
                            TableColumnAlign.Right => JustificationValues.Right,
                            TableColumnAlign.Center => JustificationValues.Center,
                            _ => JustificationValues.Left,
                        },
                    }));
                paragraph.Append(runs);
                var wordCell = new TableCell(paragraph);
                if (row.IsHeader)
                    wordCell.PrependChild(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" }));
                wordRow.Append(wordCell);
            }

            result.Append(wordRow);
        }

        return result;
    }

    private static Paragraph StyledParagraph(string styleId, IEnumerable<OpenXmlElement> runs)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        paragraph.Append(runs);
        return paragraph;
    }

    private static List<OpenXmlElement> InlineRuns(ContainerInline? inline, MainDocumentPart main, bool bold = false, bool italic = false, bool strike = false)
    {
        var runs = new List<OpenXmlElement>();
        for (var node = inline?.FirstChild; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline literal:
                    runs.Add(TextRun(literal.Content.ToString(), bold, italic, strike));
                    break;
                case TaskList task:
                    runs.Add(TextRun(task.Checked ? "☑" : "☐", bold, italic));
                    break;
                case EmphasisInline emphasis:
                    runs.AddRange(InlineRuns(emphasis, main,
                        bold || (emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2),
                        italic || (emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount is 1 or 3),
                        strike || emphasis.DelimiterChar == '~'));
                    break;
                case CodeInline code:
                    var codeRun = TextRun(code.Content, bold, italic);
                    codeRun.RunProperties!.Append(new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont });
                    runs.Add(codeRun);
                    break;
                case LinkInline { IsImage: true } image:
                    runs.Add(TextRun($"[{image.Title ?? image.Url}]", bold, italic));
                    break;
                case LinkInline link:
                    runs.Add(Hyperlink(main, link.Url, InlineRuns(link, main, bold, italic, strike)));
                    break;
                case AutolinkInline autolink:
                    runs.Add(Hyperlink(main, autolink.Url, [TextRun(autolink.Url, bold, italic)]));
                    break;
                case LineBreakInline lineBreak:
                    runs.Add(lineBreak.IsHard ? new Run(new Break()) : TextRun(" "));
                    break;
                case ContainerInline container:
                    runs.AddRange(InlineRuns(container, main, bold, italic, strike));
                    break;
            }
        }

        return runs;
    }

    private static Run TextRun(string text, bool bold = false, bool italic = false, bool strike = false)
    {
        var properties = new RunProperties();
        if (bold) properties.Append(new Bold());
        if (italic) properties.Append(new Italic());
        if (strike) properties.Append(new Strike());
        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>External http(s)/mailto links become real hyperlinks; anything else stays plain text.</summary>
    private static OpenXmlElement Hyperlink(MainDocumentPart main, string? url, List<OpenXmlElement> runs)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
            return runs.Count == 1 ? runs[0] : TextRun(string.Concat(runs.Select(r => r.InnerText)));

        var relationship = main.AddHyperlinkRelationship(uri, isExternal: true);
        var hyperlink = new Hyperlink { Id = relationship.Id };
        foreach (var run in runs.OfType<Run>())
        {
            (run.RunProperties ??= new RunProperties()).PrependChild(new RunStyle { Val = "Hyperlink" });
            hyperlink.Append(run.CloneNode(true));
        }

        return hyperlink;
    }

    private static void AddWordStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, EastAsia = EastAsiaFont, ComplexScript = BodyFont },
                new FontSize { Val = "22" },
                new Languages { Val = "en-US", EastAsia = "zh-TW" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "120", Line = "300", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        (string Size, string Before)[] headings = [("36", "360"), ("30", "280"), ("26", "240"), ("24", "200")];
        for (var level = 1; level <= headings.Length; level++)
        {
            styles.Append(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = headings[level - 1].Before, After = "120" },
                    new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(new Bold(), new Color { Val = "1F3864" }, new FontSize { Val = headings[level - 1].Size }))
            { Type = StyleValues.Paragraph, StyleId = $"Heading{level}" });
        }

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }, new ContextualSpacing()))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        styles.Append(new Style(
            new StyleName { Val = "Quote" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18U, Color = "BFBFBF", Space = 8U }),
                new Indentation { Left = "720" }),
            new StyleRunProperties(new Italic(), new Color { Val = "595959" }))
        { Type = StyleValues.Paragraph, StyleId = "Quote" });

        styles.Append(new Style(
            new StyleName { Val = "Code" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" },
                new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont }, new FontSize { Val = "19" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });

        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });

        stylesPart.Styles = styles;
    }

    /// <summary>One bullet definition shared by all bullet lists; each numbered list gets its own instance so it restarts at 1.</summary>
    private sealed class WordNumbering
    {
        public const int BulletId = 1;
        private const int BulletAbstractId = 1;
        private const int DecimalAbstractId = 2;

        private readonly Numbering _numbering;
        private int _nextId = BulletId + 1;

        public WordNumbering(MainDocumentPart main)
        {
            _numbering = new Numbering(
                AbstractNumbering(BulletAbstractId, NumberFormatValues.Bullet, ["•", "◦", "▪"]),
                AbstractNumbering(DecimalAbstractId, NumberFormatValues.Decimal, ["%1.", "%2.", "%3."]),
                new NumberingInstance(new AbstractNumId { Val = BulletAbstractId }) { NumberID = BulletId });
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = _numbering;
        }

        public int NewNumberedList()
        {
            var id = _nextId++;
            _numbering.Append(new NumberingInstance(
                new AbstractNumId { Val = DecimalAbstractId },
                new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 })
            { NumberID = id });
            return id;
        }

        private static AbstractNum AbstractNumbering(int id, NumberFormatValues format, string[] texts)
        {
            var abstractNum = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = id };
            for (var level = 0; level < texts.Length; level++)
            {
                abstractNum.Append(new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = format },
                    new LevelText { Val = texts[level] },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = (360 * (level + 1) + 360).ToString(CultureInfo.InvariantCulture), Hanging = "360" }))
                { LevelIndex = level });
            }

            return abstractNum;
        }
    }

    #endregion

    #region Excel

    /// <summary>
    /// Writes one sheet per entry (in order). Cells that look like plain numbers are stored as numbers;
    /// text starting with "=" becomes a formula, calculated by Excel when the file opens.
    /// </summary>
    public static void WriteWorkbook(string path, IReadOnlyDictionary<string, List<List<JsonElement>>> sheets)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook(new S.Sheets());
        workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = ExcelStyles();

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint sheetId = 1;
        foreach (var (requestedName, rows) in sheets)
        {
            var name = UniqueSheetName(requestedName, usedNames, sheetId);
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = BuildWorksheet(rows);
            workbookPart.Workbook.Sheets!.Append(new S.Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = sheetId++, Name = name });
        }

        workbookPart.Workbook.Append(new S.CalculationProperties { FullCalculationOnLoad = true });
        workbookPart.Workbook.Save();
    }

    private const uint HeaderStyle = 1;
    private const uint BodyStyle = 2;

    private static S.Worksheet BuildWorksheet(List<List<JsonElement>> rows)
    {
        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var widths = new double[columnCount];
        var data = new S.SheetData();

        for (var r = 0; r < rows.Count; r++)
        {
            var row = new S.Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < rows[r].Count; c++)
            {
                var (cell, display) = CellFrom(rows[r][c], $"{ColumnName(c)}{r + 1}", r == 0 ? HeaderStyle : BodyStyle);
                row.Append(cell);
                widths[c] = Math.Max(widths[c], DisplayWidth(display));
            }

            data.Append(row);
        }

        var worksheet = new S.Worksheet();
        if (rows.Count > 1)
        {
            // Freeze the header row.
            worksheet.Append(new S.SheetViews(new S.SheetView(
                new S.Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = S.PaneValues.BottomLeft, State = S.PaneStateValues.Frozen })
            { WorkbookViewId = 0U }));
        }

        if (columnCount > 0)
        {
            var columns = new S.Columns();
            for (var c = 0; c < columnCount; c++)
                columns.Append(new S.Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = Math.Clamp(widths[c] + 2, 8, 60), CustomWidth = true });
            worksheet.Append(columns);
        }

        worksheet.Append(data);
        if (rows.Count > 1 && columnCount > 0)
            worksheet.Append(new S.AutoFilter { Reference = $"A1:{ColumnName(columnCount - 1)}{rows.Count}" });

        return worksheet;
    }

    private static (S.Cell Cell, string Display) CellFrom(JsonElement value, string reference, uint style)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };

        // Numbers become numeric cells (so they sum and sort), except values like "007" or long IDs
        // where the text form matters.
        var isNumber = value.ValueKind == JsonValueKind.Number ||
            (text.Length is > 0 and < 16 && !(text.Length > 1 && text[0] == '0' && text[1] != '.') &&
             double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

        if (value.ValueKind == JsonValueKind.String && text.Length > 1 && text[0] == '=' && style != HeaderStyle)
            return (new S.Cell { CellReference = reference, CellFormula = new S.CellFormula(text[1..]), StyleIndex = style }, text);

        var cell = isNumber && style != HeaderStyle
            ? new S.Cell { CellReference = reference, DataType = S.CellValues.Number, CellValue = new S.CellValue(text), StyleIndex = style }
            : new S.Cell { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)), StyleIndex = style };
        return (cell, text);
    }

    private static S.Stylesheet ExcelStyles()
    {
        var thin = new Func<S.BorderPropertiesType, S.BorderPropertiesType>(b =>
        {
            b.Style = S.BorderStyleValues.Thin;
            b.Color = new S.Color { Rgb = "FFBFBFBF" };
            return b;
        });

        return new S.Stylesheet(
            new S.Fonts(
                new S.Font(new S.FontSize { Val = 11D }, new S.FontName { Val = BodyFont }),
                new S.Font(new S.Bold(), new S.FontSize { Val = 11D }, new S.FontName { Val = BodyFont })),
            new S.Fills(
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }),
                new S.Fill(new S.PatternFill(new S.ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = S.PatternValues.Solid })),
            new S.Borders(
                new S.Border(),
                new S.Border(thin(new S.LeftBorder()), thin(new S.RightBorder()), thin(new S.TopBorder()), thin(new S.BottomBorder()), new S.DiagonalBorder())),
            new S.CellFormats(
                new S.CellFormat(),
                new S.CellFormat { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true },
                new S.CellFormat { BorderId = 1, ApplyBorder = true }));
    }

    /// <summary>Excel sheet names: max 31 chars, none of []:*?/\ , unique within the workbook.</summary>
    private static string UniqueSheetName(string requested, HashSet<string> used, uint index)
    {
        var cleaned = new string(requested.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = $"Sheet{index}";
        if (cleaned.Length > 31)
            cleaned = cleaned[..31];

        var name = cleaned;
        for (var n = 2; !used.Add(name); n++)
            name = $"{cleaned[..Math.Min(cleaned.Length, 28)]} ({n})";
        return name;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Rough Excel column width: CJK and other full-width characters count double.</summary>
    private static double DisplayWidth(string text) =>
        text.Sum(c => c >= 0x1100 && (c <= 0x115F || c >= 0x2E80) ? 2.0 : 1.0);

    #endregion
}
