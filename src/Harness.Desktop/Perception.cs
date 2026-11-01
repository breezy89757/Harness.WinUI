// Harness.WinUI — Licensed under the MIT License.

using System.Drawing;
using System.Drawing.Drawing2D;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;

namespace Harness.Desktop;

/// <summary>One thing on the window the agent may act on. Source: uia (a real control) or ocr (text found in the picture).</summary>
internal sealed class UiElement
{
    public int Id { get; set; }
    public string Source { get; init; } = "uia";
    public string Kind { get; init; } = "";       // button, edit, text … ("text" for OCR lines)
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public Rectangle Rect { get; init; }          // screen pixels
    public string Patterns { get; init; } = "";   // invoke, value, toggle …
    public string Value { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Actionable { get; init; } = true; // false for text shown only so the agent can read it
    public AutomationElement? Element { get; init; }

    public Point Center => new(Rect.Left + Rect.Width / 2, Rect.Top + Rect.Height / 2);

    public string Describe()
    {
        var bits = new List<string> { $"[{Id}]", Source == "ocr" ? "ocr-text" : Kind };
        if (Name.Length > 0) bits.Add($"\"{Name}\"");
        if (AutomationId.Length > 0) bits.Add($"id={AutomationId}");
        if (Value.Length > 0 && Value != Name) bits.Add($"value=\"{Value}\"");
        if (Patterns.Length > 0) bits.Add($"({Patterns})");
        if (!Enabled) bits.Add("(disabled)");
        if (!Actionable) bits.Add("(read only)");
        return string.Join(' ', bits);
    }

    /// <summary>What identifies the element across turns, when the ids are renumbered.</summary>
    public string Signature => $"{Source}|{Kind}|{AutomationId}|{Name}";
}

/// <param name="Image">The screenshot with the numbered marks drawn on it (what the model is shown).</param>
/// <param name="Plain">The same screenshot without marks (what is compared to see whether the screen changed).</param>
internal sealed record Snapshot(List<UiElement> Elements, Bitmap Image, Bitmap Plain, Rectangle Region, string OcrNote) : IDisposable
{
    public void Dispose()
    {
        Image.Dispose();
        Plain.Dispose();
    }
}

internal static class Perception
{
    private static readonly HashSet<ControlType> Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink, ControlType.ListItem,
        ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
        ControlType.SplitButton, ControlType.Spinner, ControlType.Document, ControlType.Menu,
    ];

    public static async Task<Snapshot> TakeAsync(AutomationElement window, bool ocr, int maxElements = 150)
    {
        var region = window.Properties.BoundingRectangle.ValueOrDefault;
        var elements = CollectUia(window, maxElements);
        using var raw = Capture.Rectangle(region).Bitmap;
        var plain = new Bitmap(raw);
        var image = new Bitmap(raw); // a copy we are free to draw on
        var note = "";
        if (ocr)
        {
            var (lines, engineNote) = await OcrReader.ReadAsync(plain).ConfigureAwait(false);
            note = engineNote;
            foreach (var line in lines)
            {
                var rect = new Rectangle(region.Left + line.Rect.Left, region.Top + line.Rect.Top, line.Rect.Width, line.Rect.Height);
                var c = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                // Only text that no real control already explains.
                if (elements.Any(e => e.Name.Length > 0 && e.Rect.Contains(c)))
                    continue;
                elements.Add(new UiElement { Source = "ocr", Kind = "text", Name = line.Text, Rect = rect });
            }
        }
        for (var i = 0; i < elements.Count; i++)
            elements[i].Id = i + 1;
        Mark(image, elements, region);
        return new Snapshot(elements, image, plain, region, note);
    }

    private static List<UiElement> CollectUia(AutomationElement window, int max)
    {
        var found = new List<UiElement>();
        var texts = 0;
        foreach (var e in window.FindAllDescendants())
        {
            if (found.Count >= max) break;
            try
            {
                var type = e.Properties.ControlType.ValueOrDefault;
                var isText = type == ControlType.Text;
                if (!isText && !Interactive.Contains(type)) continue;
                if (e.Properties.IsOffscreen.ValueOrDefault) continue;
                var rect = e.Properties.BoundingRectangle.ValueOrDefault;
                if (rect.Width < 4 || rect.Height < 4) continue;
                var name = (e.Properties.Name.ValueOrDefault ?? "").Trim();
                if (isText && (name.Length == 0 || texts >= 40)) continue;
                if (isText) texts++;

                var patterns = new List<string>();
                if (e.Patterns.Invoke.IsSupported) patterns.Add("invoke");
                if (e.Patterns.Toggle.IsSupported) patterns.Add("toggle");
                if (e.Patterns.SelectionItem.IsSupported) patterns.Add("select");
                if (e.Patterns.ExpandCollapse.IsSupported) patterns.Add("expand");
                var valuePattern = e.Patterns.Value.PatternOrDefault;
                var readOnly = valuePattern?.IsReadOnly.ValueOrDefault ?? true;
                if (valuePattern != null && !readOnly) patterns.Add("value");
                var value = valuePattern?.Value.ValueOrDefault ?? "";

                found.Add(new UiElement
                {
                    Kind = type.ToString().ToLowerInvariant(),
                    Name = name.Length > 80 ? name[..80] : name,
                    AutomationId = e.Properties.AutomationId.ValueOrDefault ?? "",
                    Rect = rect,
                    Patterns = string.Join(',', patterns),
                    Value = value.Length > 80 ? value[..80] : value,
                    Enabled = e.Properties.IsEnabled.ValueOrDefault,
                    Actionable = !isText,
                    Element = e,
                });
            }
            catch
            {
                // an element that disappears or does not answer while we read it is simply left out
            }
        }
        return found;
    }

    /// <summary>Numbered boxes on the screenshot (the "marks"): red = a control, grey = text to read, blue = text found by OCR.</summary>
    private static void Mark(Bitmap image, List<UiElement> elements, Rectangle region)
    {
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", Math.Max(9f, image.Width / 110f), FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var e in elements)
        {
            var color = e.Source == "ocr" ? Color.RoyalBlue : e.Actionable ? Color.Crimson : Color.DimGray;
            var r = new Rectangle(e.Rect.Left - region.Left, e.Rect.Top - region.Top, e.Rect.Width, e.Rect.Height);
            using var pen = new Pen(color, 2);
            g.DrawRectangle(pen, r);
            var label = e.Id.ToString();
            var size = g.MeasureString(label, font);
            var tag = new RectangleF(r.Left, Math.Max(0, r.Top - size.Height), size.Width + 4, size.Height);
            using var fill = new SolidBrush(color);
            g.FillRectangle(fill, tag);
            g.DrawString(label, font, Brushes.White, tag.Left + 2, tag.Top);
        }
    }

    /// <summary>PNG bytes, scaled down to at most <paramref name="maxWidth"/> pixels wide (marks are already drawn, so they scale with it).</summary>
    public static byte[] Png(Bitmap image, int maxWidth = 1400)
    {
        var scale = image.Width > maxWidth ? maxWidth / (double)image.Width : 1.0;
        using var scaled = scale < 1 ? new Bitmap(image, (int)(image.Width * scale), (int)(image.Height * scale)) : null;
        using var ms = new MemoryStream();
        (scaled ?? image).Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>A rough fingerprint of the picture (32x32 grey levels) to tell whether the screen changed.</summary>
    public static byte[] Thumbprint(Bitmap image)
    {
        using var small = new Bitmap(image, 32, 32);
        var bytes = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                bytes[y * 32 + x] = (byte)((c.R + c.G + c.B) / 3);
            }
        return bytes;
    }

    public static double Difference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }
}
