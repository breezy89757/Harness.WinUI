// Harness.WinUI — Licensed under the MIT License.

using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using Drawing = DocumentFormat.OpenXml.Drawing;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace Harness.Core.Files;

/// <summary>
/// Plain-text extraction for Office and PDF files, so the model can read reports, spreadsheets and
/// slides directly (a generic file server only returns their raw bytes). Output stops at a character
/// budget so one large file can't flood the context.
/// </summary>
public static class DocumentText
{
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx", ".pdf" };

    public static bool IsDocument(string path) => s_extensions.Contains(Path.GetExtension(path));

    public static string Extract(string path, int maxChars)
    {
        var text = new BoundedText(maxChars);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx":
                ExtractWord(path, text);
                break;
            case ".xlsx":
                ExtractExcel(path, text);
                break;
            case ".pptx":
                ExtractPowerPoint(path, text);
                break;
            case ".pdf":
                ExtractPdf(path, text);
                break;
        }

        return text.ToString();
    }

    private static void ExtractWord(string path, BoundedText text)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        foreach (var paragraph in document.MainDocumentPart?.Document?.Body?.Descendants<Word.Paragraph>() ?? [])
        {
            if (!text.AppendLine(paragraph.InnerText))
                return;
        }
    }

    private static void ExtractExcel(string path, BoundedText text)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets is not { } sheets)
            return;

        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(item => item.InnerText).ToArray() ?? [];

        foreach (var sheet in sheets.Elements<Sheet>())
        {
            if (sheet.Id?.Value is not { } relationshipId || workbook.GetPartById(relationshipId) is not WorksheetPart part)
                continue;
            if (!text.AppendLine($"## {sheet.Name}"))
                return;

            foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
            {
                var cells = row.Elements<Cell>().Select(cell => CellText(cell, sharedStrings));
                if (!text.AppendLine(string.Join('\t', cells)))
                    return;
            }
        }
    }

    private static string CellText(Cell cell, string[] sharedStrings)
    {
        var raw = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
        return cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var index) && index < sharedStrings.Length
            ? sharedStrings[index]
            : raw;
    }

    private static void ExtractPowerPoint(string path, BoundedText text)
    {
        using var document = PresentationDocument.Open(path, isEditable: false);
        var presentation = document.PresentationPart;
        var slideIds = presentation?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [];

        var number = 0;
        foreach (var slideId in slideIds)
        {
            if (slideId.RelationshipId?.Value is not { } id || presentation!.GetPartById(id) is not SlidePart slide)
                continue;
            if (!text.AppendLine($"## Slide {++number}"))
                return;

            foreach (var paragraph in slide.Slide?.Descendants<Drawing.Paragraph>() ?? [])
            {
                if (!text.AppendLine(string.Concat(paragraph.Descendants<Drawing.Text>().Select(t => t.Text))))
                    return;
            }
        }
    }

    private static void ExtractPdf(string path, BoundedText text)
    {
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            if (!text.AppendLine($"## Page {page.Number}") ||
                !text.AppendLine(NormalizeRadicals(string.Join(' ', page.GetWords().Select(w => w.Text)))))
            {
                return;
            }
        }
    }

    /// <summary>
    /// PDFs printed by Chromium/Edge often map CJK glyphs to Kangxi radical code points (U+2F8F "⾏"
    /// instead of U+884C "行"): identical on screen, but they break searching and matching. NFKC maps
    /// them back; it's applied only to the radical blocks so full-width punctuation is left alone.
    /// </summary>
    private static string NormalizeRadicals(string text)
    {
        if (!text.Any(IsRadical))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(IsRadical(c) ? c.ToString().Normalize(NormalizationForm.FormKC) : c);
        return builder.ToString();
    }

    private static bool IsRadical(char c) => c is >= '⺀' and <= '⿟';

    /// <summary>StringBuilder that stops accepting text once the budget is spent (and says so).</summary>
    private sealed class BoundedText(int maxChars)
    {
        private readonly StringBuilder _builder = new();
        private bool _truncated;

        public bool AppendLine(string line)
        {
            if (_truncated)
                return false;
            if (string.IsNullOrWhiteSpace(line))
                return true;

            if (_builder.Length + line.Length + 1 > maxChars)
            {
                _builder.Append(line.AsSpan(0, Math.Max(0, maxChars - _builder.Length))).AppendLine();
                _builder.AppendLine("[… truncated]");
                _truncated = true;
                return false;
            }

            _builder.AppendLine(line);
            return true;
        }

        public override string ToString() => _builder.ToString();
    }
}
