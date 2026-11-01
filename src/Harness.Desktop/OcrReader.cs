// Harness.WinUI — Licensed under the MIT License.

using System.Drawing;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Harness.Desktop;

internal sealed record OcrLine(string Text, Rectangle Rect);

/// <summary>Text in a picture, found by Windows' own OCR: offline, nothing leaves the PC. Needs the language pack of the text to be installed.</summary>
internal static class OcrReader
{
    private static readonly string[] Languages = ["zh-Hant-TW", "zh-Hant", "zh-TW", "en-US"];

    public static async Task<(List<OcrLine> Lines, string Note)> ReadAsync(Bitmap image)
    {
        OcrEngine? engine = null;
        var used = "";
        foreach (var tag in Languages)
        {
            try
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language) && (engine = OcrEngine.TryCreateFromLanguage(language)) != null)
                {
                    used = tag;
                    break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // not a valid tag on this system; try the next
            }
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            return ([], "OCR is not available: no language pack is installed.");
        used = used.Length > 0 ? used : engine.RecognizerLanguage.LanguageTag;

        // Small UI text reads much better at twice the size.
        const int scale = 2;
        using var big = new Bitmap(image, image.Width * scale, image.Height * scale);
        using var ms = new MemoryStream();
        big.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(software);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var left = line.Words.Min(w => w.BoundingRect.Left);
            var top = line.Words.Min(w => w.BoundingRect.Top);
            var right = line.Words.Max(w => w.BoundingRect.Right);
            var bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            // Chinese comes back one character per word: a space goes between two words only when both sides are Latin letters or digits.
            var sb = new StringBuilder();
            foreach (var w in line.Words)
            {
                if (w.Text.Length == 0) continue;
                if (sb.Length > 0 && char.IsAsciiLetterOrDigit(sb[^1]) && char.IsAsciiLetterOrDigit(w.Text[0]))
                    sb.Append(' ');
                sb.Append(w.Text);
            }
            var text = sb.ToString().Trim();
            if (text.Length == 0) continue;
            lines.Add(new OcrLine(text, new Rectangle((int)(left / scale), (int)(top / scale), (int)((right - left) / scale), (int)((bottom - top) / scale))));
        }
        return (lines, $"OCR language {used}: {lines.Count} lines of text found.");
    }
}
