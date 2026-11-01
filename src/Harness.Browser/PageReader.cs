// Harness.WinUI — Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Harness.Browser;

/// <summary>An element the agent may act on, as the page reported it. Ref is its number in the list the model sees.</summary>
internal sealed record PageElement(
    int Ref, string Tag, string Type, string Role, string Name, IReadOnlyList<string> Names, string Label, string Placeholder,
    string TestId, string Id, string Value, bool Checked, bool Disabled, bool IsPassword, string Text)
{
    /// <summary>What identifies it across turns, when the numbers are given again.</summary>
    public string Signature => $"{Role}|{Name}|{Id}|{TestId}";

    public string Describe()
    {
        var bits = new List<string> { $"[{Ref}]", Role.Length > 0 ? Role : Tag };
        if (Name.Length > 0) bits.Add($"\"{Name}\"");
        if (Placeholder.Length > 0 && Placeholder != Name) bits.Add($"placeholder=\"{Placeholder}\"");
        if (Value.Length > 0) bits.Add($"value=\"{Value}\"");
        if (Type is "checkbox" or "radio" || Role is "checkbox" or "radio" or "switch") bits.Add(Checked ? "(checked)" : "(unchecked)");
        if (Disabled) bits.Add("(disabled)");
        return string.Join(' ', bits);
    }
}

/// <summary>What the model is shown for one turn.</summary>
/// <param name="Png">The viewport with the numbered marks drawn on it; null when no screenshot could be taken.</param>
internal sealed record PageObservation(string Url, string Title, string Aria, IReadOnlyList<PageElement> Elements, byte[]? Png, string Fingerprint)
{
    /// <summary>The text part of the turn's message: where we are, the page's structure, and the numbered elements.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {Url}").AppendLine($"Title: {Title}").AppendLine();
        sb.AppendLine("頁面結構（ARIA snapshot，節錄）：").AppendLine(Aria.Length > 0 ? Aria : "(空)").AppendLine();
        sb.AppendLine("可操作的東西（用編號指定）：");
        if (Elements.Count == 0)
            sb.AppendLine("(沒有)");
        foreach (var e in Elements)
            sb.AppendLine(e.Describe());
        return sb.ToString();
    }
}

internal static class PageReader
{
    public const int MaxElements = 120;
    public const int MaxAriaChars = 3500;

    // Collects the visible things a person can act on, numbers them (data-hqa-ref) and says what each one is called. A password field's value is never read.
    private const string CollectJs = """
        () => {
          document.querySelectorAll('[data-hqa-ref]').forEach(e => e.removeAttribute('data-hqa-ref'));
          const SEL = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=switch],[role=option],[role=textbox],[role=combobox],[role=searchbox],[contenteditable=""],[contenteditable=true],[onclick]';
          const visible = e => { const r = e.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false; const s = getComputedStyle(e); return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
          const clean = s => (s || '').trim().replace(/\s+/g, ' ');
          const textOf = e => clean(e.innerText || e.textContent);
          const labelOf = e => {
            if (e.labels && e.labels.length) return textOf(e.labels[0]);
            const ids = e.getAttribute('aria-labelledby');
            if (ids) { const l = document.getElementById(ids.split(' ')[0]); if (l) return textOf(l); }
            return '';
          };
          const roleOf = (e, tag, type) => {
            const explicit = e.getAttribute('role'); if (explicit) return explicit.split(' ')[0];
            if (tag === 'a') return 'link';
            if (tag === 'button' || tag === 'summary') return 'button';
            if (tag === 'select') return e.multiple || e.size > 1 ? 'listbox' : 'combobox';
            if (tag === 'textarea') return 'textbox';
            if (tag === 'input') {
              if (['submit', 'button', 'reset', 'image'].includes(type)) return 'button';
              if (type === 'checkbox') return 'checkbox';
              if (type === 'radio') return 'radio';
              if (type === 'range') return 'slider';
              if (type === 'number') return 'spinbutton';
              if (type === 'search') return 'searchbox';
              return 'textbox';
            }
            return '';
          };
          const out = []; let n = 0;
          for (const e of document.querySelectorAll(SEL)) {
            if (!visible(e)) continue;
            const tag = e.tagName.toLowerCase();
            const type = (e.getAttribute('type') || '').toLowerCase();
            const text = textOf(e).slice(0, 80);
            const label = labelOf(e);
            const names = [];
            for (const c of [e.getAttribute('aria-label'), label, text, e.getAttribute('title'), e.getAttribute('alt'), (['submit','button','reset'].includes(type) ? e.value : ''), e.getAttribute('placeholder')]) {
              const v = clean(c); if (v && v.length <= 100 && !names.includes(v)) names.push(v);
            }
            const isPassword = type === 'password';
            let value = '';
            if (!isPassword) {
              if (tag === 'select') value = e.selectedOptions && e.selectedOptions[0] ? clean(e.selectedOptions[0].text) : '';
              else if (tag === 'input' || tag === 'textarea') value = clean(e.value).slice(0, 80);
            }
            e.setAttribute('data-hqa-ref', String(n));
            out.push({ ref: n, tag, type, role: roleOf(e, tag, type), name: names[0] || '', names, label, placeholder: e.getAttribute('placeholder') || '',
                       testid: e.getAttribute('data-testid') || e.getAttribute('data-test') || e.getAttribute('data-qa') || '', id: e.id || '',
                       value, checked: !!e.checked || e.getAttribute('aria-checked') === 'true', disabled: !!e.disabled || e.getAttribute('aria-disabled') === 'true',
                       isPassword, text });
            if (++n >= __MAX__) break;
          }
          return out;
        }
        """;

    // Draws a numbered box on every marked element that is on screen, and a black cover over every password field and every field that holds a secret.
    private const string MarkJs = """
        (secretValues) => {
          const old = document.getElementById('__hqa_marks'); if (old) old.remove();
          const c = document.createElement('div'); c.id = '__hqa_marks';
          c.style.cssText = 'position:fixed;left:0;top:0;width:100vw;height:100vh;pointer-events:none;z-index:2147483647;';
          const vw = window.innerWidth, vh = window.innerHeight;
          for (const e of document.querySelectorAll('[data-hqa-ref]')) {
            const r = e.getBoundingClientRect();
            if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) continue;
            const hide = (e.tagName === 'INPUT' && e.type === 'password') || ((e.tagName === 'INPUT' || e.tagName === 'TEXTAREA') && e.value && secretValues.includes(e.value));
            const b = document.createElement('div');
            b.style.cssText = `position:absolute;left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px;box-sizing:border-box;border:2px solid #d6204a;${hide ? 'background:#000;' : ''}`;
            const t = document.createElement('div');
            t.textContent = e.getAttribute('data-hqa-ref');
            t.style.cssText = `position:absolute;left:-2px;top:${r.top < 14 ? 0 : -14}px;background:#d6204a;color:#fff;font:bold 11px/14px sans-serif;padding:0 3px;`;
            b.appendChild(t); c.appendChild(b);
          }
          for (const e of document.querySelectorAll('input[type=password]:not([data-hqa-ref])')) {
            const r = e.getBoundingClientRect(); if (r.width < 2) continue;
            const b = document.createElement('div');
            b.style.cssText = `position:absolute;left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px;background:#000;`;
            c.appendChild(b);
          }
          document.body.appendChild(c);
        }
        """;

    private const string UnmarkJs = "() => { const m = document.getElementById('__hqa_marks'); if (m) m.remove(); }";

    /// <summary>Looks at the page: the numbered elements, the page's structure, and a screenshot with the numbers drawn on it. Secrets are replaced by their placeholders everywhere the model can read.</summary>
    public static async Task<PageObservation> ObserveAsync(IPage page, IReadOnlyDictionary<string, string> secrets, bool screenshot)
    {
        var raw = await page.EvaluateAsync<JsonElement>(CollectJs.Replace("__MAX__", MaxElements.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ConfigureAwait(false);
        string Mask(string s) => secrets.Count == 0 ? s : BrowserSecrets.Mask(s, secrets);
        var elements = new List<PageElement>();
        foreach (var e in raw.EnumerateArray())
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var names = e.TryGetProperty("names", out var ns) && ns.ValueKind == JsonValueKind.Array ? ns.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            elements.Add(new PageElement(
                e.GetProperty("ref").GetInt32(), S("tag"), S("type"), S("role"), Mask(S("name")), [.. names.Select(Mask)], Mask(S("label")), Mask(S("placeholder")),
                S("testid"), S("id"), Mask(S("value")), B("checked"), B("disabled"), B("isPassword"), Mask(S("text"))));
        }

        var aria = "";
        try
        {
            aria = await page.Locator("body").AriaSnapshotAsync(new() { Timeout = 3000 }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // some pages give no snapshot: the numbered elements are still there
        }
        aria = Mask(aria.Length > MaxAriaChars ? aria[..MaxAriaChars] + "\n…" : aria);

        byte[]? png = null;
        if (screenshot)
        {
            try
            {
                await page.EvaluateAsync(MarkJs, secrets.Values.Where(v => v.Length >= 3).ToArray()).ConfigureAwait(false);
                png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                png = null;
            }
            finally
            {
                try
                {
                    await page.EvaluateAsync(UnmarkJs).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaywrightException)
                {
                    // the page went away while we were drawing: nothing to clean up
                }
            }
        }

        var title = Mask(await page.TitleAsync().ConfigureAwait(false));
        return new PageObservation(page.Url, title, aria, elements, png, Fingerprint(page.Url, title, aria, elements));
    }

    /// <summary>What "the screen changed" means: another address, another structure, or any element with another name, value or state.</summary>
    internal static string Fingerprint(string url, string title, string aria, IReadOnlyList<PageElement> elements)
    {
        var sb = new StringBuilder().Append(url).Append('\n').Append(title).Append('\n').Append(aria).Append('\n');
        foreach (var e in elements)
            sb.Append(e.Role).Append('|').Append(e.Name).Append('|').Append(e.Value).Append('|').Append(e.Checked).Append('|').Append(e.Disabled).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
