// Harness.WinUI — Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Harness.Core.Artifacts;

namespace Harness.WinUI.Artifacts;

/// <summary>
/// The page that shows a <see cref="SlideDeck"/>: the slide big, a strip of all slides below, arrow keys to move, a presenting mode, the
/// speaker notes, and, for a finished deck, editing in place. The same page is used while the deck is still being written (it is loaded
/// once and given the slides as they come with <c>setDeck</c>, so each slide shows up when it is finished) and for the finished deck.
/// A slide is drawn from the deck's <see cref="BrandTheme"/> (given with the deck): its size, the logos and lines its layout carries,
/// where the title and the text go and how they look, all in points, then scaled to fit; the same numbers the .pptx is made from.
/// Editing: the text of the slide on screen can be clicked and changed; every change is sent to the app as a small message (see
/// <see cref="SlideEdit"/>), which keeps the deck, and structural changes (a new line, a new slide …) come back as a new deck to draw.
/// </summary>
public static class SlidesPreview
{
    /// <param name="follow">Show the newest slide as it arrives (while writing); false opens at the first slide.</param>
    /// <param name="edit">The text can be edited (a finished deck, shown where the page may send messages to the app).</param>
    public static string Page(string title, string? deckJson, bool follow, bool edit = false)
    {
        var labels = JsonSerializer.Serialize(new
        {
            writing = Strings.SlideWriting,
            notes = Strings.SlideNotes,
            present = Strings.SlidePresent,
            previous = Strings.SlidePrevious,
            next = Strings.SlideNext,
            empty = Strings.SlideEmpty,
            addSlide = Strings.SlideAdd,
            duplicate = Strings.SlideDuplicate,
            deleteSlide = Strings.SlideDelete,
            moveLeft = Strings.SlideMoveEarlier,
            moveRight = Strings.SlideMoveLater,
            newSlide = Strings.SlideNewTitle,
            editHint = Strings.SlideEditHint,
            notesHint = Strings.SlideNotesHint,
        });
        var deck = (deckJson ?? SlideDeck.Parse(string.Empty, isComplete: false).ToJson()).Replace("</", "<\\/", StringComparison.Ordinal);
        return Template
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("{{LABELS}}", labels.Replace("</", "<\\/", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{{FOLLOW}}", follow ? "true" : "false", StringComparison.Ordinal)
            .Replace("{{EDIT}}", edit ? "true" : "false", StringComparison.Ordinal)
            .Replace("{{DECK}}", deck, StringComparison.Ordinal);
    }

    private const string Template = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{TITLE}}</title>
        <meta name="color-scheme" content="light dark">
        <style>
        :root{--accent:#1f4e79;--muted:#6b7280;--bg:#eef0f3;--panel:#ffffff;--text:#1f2937}
        @media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--panel:#2b2b2e;--text:#e5e7eb}}
        *{box-sizing:border-box}
        html,body{margin:0;height:100%;background:var(--bg);color:var(--text);font:14px 'Segoe UI','Microsoft JhengHei UI',sans-serif;overflow:hidden}
        #app{display:flex;flex-direction:column;height:100%}
        #bar{display:flex;align-items:center;gap:4px;padding:6px 12px}
        #bar .spacer{flex:1}
        #bar .hint{color:var(--muted);font-size:12px;margin-left:10px}
        #bar .sep{width:1px;height:18px;background:rgba(127,127,127,.35);margin:0 6px}
        #bar button{border:0;background:transparent;color:inherit;border-radius:6px;padding:5px 9px;cursor:pointer;font:inherit}
        #bar button:hover{background:rgba(127,127,127,.18)}
        #bar button[aria-pressed=true]{background:rgba(127,127,127,.25)}
        #bar button:disabled{opacity:.35;cursor:default;background:transparent}
        #count{min-width:64px;text-align:center;color:var(--muted)}
        #stage{flex:1;min-height:0;display:flex;align-items:center;justify-content:center;padding:6px 16px 10px}
        #fit{position:relative;box-shadow:0 2px 14px rgba(0,0,0,.25);background:#fff}
        #notes{max-height:26%;overflow:auto;margin:0 16px 8px;padding:8px 12px;background:var(--panel);border-radius:8px;white-space:pre-wrap;display:none;font-size:13px}
        #notes.on{display:block}
        #notes b{display:block;color:var(--muted);font-weight:600;margin-bottom:2px}
        #notes textarea{width:100%;min-height:64px;max-height:160px;resize:vertical;border:1px solid rgba(127,127,127,.35);border-radius:6px;padding:6px 8px;background:transparent;color:inherit;font:inherit}
        #strip{display:flex;gap:10px;overflow-x:auto;padding:10px 16px 12px;border-top:1px solid rgba(127,127,127,.25);background:var(--panel)}
        .thumb{position:relative;flex:none;width:160px;border:2px solid transparent;border-radius:4px;cursor:pointer;overflow:hidden;background:#fff}
        .thumb.sel{border-color:var(--accent)}
        .thumb .n{position:absolute;left:3px;bottom:2px;z-index:2;font-size:10px;background:rgba(0,0,0,.55);color:#fff;border-radius:3px;padding:0 4px}
        .thumb .slide{transform-origin:0 0;pointer-events:none}
        .thumb.writing{animation:pulse 1.4s ease-in-out infinite}
        @keyframes pulse{50%{opacity:.55}}
        body.present #bar,body.present #strip,body.present #notes{display:none}
        body.present #stage{padding:0;background:#000}
        body.present #fit{box-shadow:none}
        /* a slide: its size and everything on it come from the theme, in points */
        .slide{position:relative;background:#fff;color:#1f2937;overflow:hidden;user-select:none;-webkit-user-select:none}
        .slide .abs{position:absolute}
        .slide .ph{position:absolute;display:flex;line-height:1.2;overflow:hidden}
        .slide .ph>*{width:100%}
        .slide .body{position:absolute;overflow:hidden}
        .slide .li{position:relative;margin-top:.32em;line-height:1.3}
        .slide .li .mk{position:absolute;color:var(--mark,#1f4e79)}
        .slide .li.num .mk{color:inherit}
        .slide .p{margin-top:.32em;line-height:1.3}
        .slide .body>:first-child{margin-top:0}
        .slide table{border-collapse:collapse;width:100%;margin-top:14px;table-layout:fixed}
        .slide td,.slide th{border:.75px solid #d1d5db;padding:4px 7px;text-align:left;vertical-align:middle;line-height:1.25}
        .slide th{background:var(--mark,#1f4e79);color:#fff;font-weight:700}
        .slide code{font-family:Consolas,monospace}
        .slide img.abs{object-fit:fill}
        .slide .badge{position:absolute;right:16px;top:12px;font:600 12px 'Segoe UI',sans-serif;color:#fff;background:#1f4e79;border-radius:10px;padding:2px 10px;opacity:.9}
        .slide [data-e]{user-select:text;-webkit-user-select:text;cursor:text;outline:none;border-radius:3px;min-width:1em}
        .slide [data-e]:hover{box-shadow:0 0 0 1px rgba(31,78,121,.4)}
        .slide [data-e]:focus{box-shadow:0 0 0 2px #1f4e79;background:rgba(31,78,121,.06)}
        .slide th [data-e]:focus{background:rgba(255,255,255,.18)}
        .slide [data-e]:empty::before{content:'…';color:#9ca3af}
        #empty{color:var(--muted);padding:40px;text-align:center}
        </style></head><body>
        <div id="app">
          <div id="bar">
            <button id="prev" title="" aria-label="">&#x2039;</button><span id="count"></span><button id="next" title="" aria-label="">&#x203A;</button>
            <span class="sep editonly"></span>
            <button id="addBtn" class="editonly">&#xFF0B;</button><button id="dupBtn" class="editonly">&#x29C9;</button><button id="delBtn" class="editonly">&#x1F5D1;</button>
            <button id="leftBtn" class="editonly">&#x25C2;</button><button id="rightBtn" class="editonly">&#x25B8;</button>
            <span class="hint editonly" id="hint"></span>
            <span class="spacer"></span>
            <button id="notesBtn" aria-pressed="false"></button>
            <button id="presentBtn"></button>
          </div>
          <div id="stage"><div id="fit"></div></div>
          <div id="notes"></div>
          <div id="strip"></div>
        </div>
        <script>
        const L = {{LABELS}};
        const EDIT = {{EDIT}};
        let deck = {{DECK}};
        let current = 0, follow = {{FOLLOW}}, showNotes = false, pendingFocus = null;
        const $ = id => document.getElementById(id);
        const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
        const runs = rs => (rs || []).map(r => { let t = esc(r.text); if (r.code) t = '<code>' + t + '</code>'; if (r.italic) t = '<i>' + t + '</i>'; if (r.bold) t = '<b>' + t + '</b>'; return t; }).join('');
        const post = op => { try { window.chrome.webview.postMessage(op); } catch (e) { } };
        const T = () => deck.theme;

        // the same marks the app reads (SlideDeck.Runs)
        function parseRuns(md) {
          md = md.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1');
          const out = [], re = /\*\*(.+?)\*\*|(?<!\*)\*([^*\s](?:[^*]*?[^*\s])?)\*(?!\*)|`([^`]+)`/g; let pos = 0, m;
          while ((m = re.exec(md))) {
            if (m.index > pos) out.push({ text: md.slice(pos, m.index) });
            if (m[1] !== undefined) out.push({ text: m[1], bold: true }); else if (m[2] !== undefined) out.push({ text: m[2], italic: true }); else out.push({ text: m[3], code: true });
            pos = m.index + m[0].length;
          }
          if (pos < md.length) out.push({ text: md.slice(pos) });
          return out.length ? out : [{ text: '' }];
        }
        function toMd(el) {
          let s = '';
          el.childNodes.forEach(n => {
            if (n.nodeType === 3) { s += n.textContent; return; }
            if (n.nodeName === 'BR') { s += ' '; return; }
            const t = toMd(n), tag = n.nodeName;
            if (!t) return;
            s += (tag === 'B' || tag === 'STRONG') ? '**' + t + '**' : (tag === 'I' || tag === 'EM') ? '*' + t + '*' : tag === 'CODE' ? '`' + t + '`' : t;
          });
          return s;
        }

        // ---- drawing a slide from the theme ----
        const J = { left: 'flex-start', center: 'center', right: 'flex-end', top: 'flex-start', middle: 'center', bottom: 'flex-end' };
        const frame = f => 'left:' + f.x + 'px;top:' + f.y + 'px;width:' + f.w + 'px;height:' + f.h + 'px;';
        const look = l => 'font-family:' + l.font + ';font-size:' + l.size + 'px;color:#' + l.color + ';font-weight:' + (l.bold ? 700 : 400) + ';text-align:' + l.align + ';justify-content:' + J[l.align] + ';align-items:' + J[l.anchor] + ';';
        const layoutOf = s => s.kind === 'title' ? T().title : s.kind === 'section' ? (T().section || T().title) : T().content;
        const role = (layout, r) => (layout.placeholders || []).find(p => p.role === r);
        // an editable piece of text carries data-e and where it belongs (data-t: title, subtitle, block, cell)
        const ed = (edit, attrs) => edit ? ' contenteditable="true" spellcheck="false" data-e ' + attrs : '';

        function shapesHtml(layout, s) {
          return (layout.shapes || []).map(sh => {
            if (sh.kind === 'image') return '<img class="abs" src="' + esc((deck.imageBase || '') + sh.image) + '" style="' + frame(sh.frame) + '">';
            if (sh.kind === 'line') { const f = sh.frame, w = Math.max(sh.weight || 0.75, 0.75), v = f.w < 0.5; return '<div class="abs" style="left:' + f.x + 'px;top:' + f.y + 'px;width:' + (v ? w : f.w) + 'px;height:' + (v ? f.h : w) + 'px;background:#' + sh.color + '"></div>'; }
            if (sh.kind === 'rect') return '<div class="abs" style="' + frame(sh.frame) + 'background:#' + sh.color + '"></div>';
            if (sh.kind === 'pageNumber') return '<div class="ph" style="' + frame(sh.frame) + look(sh.text) + '"><span>' + s.number + '</span></div>';
            return '';
          }).join('');
        }

        // ---- charts: drawn as SVG from the chart's numbers (the .pptx gets a real PowerPoint chart of the same) ----
        function niceScale(min, max, n) {
          if (min === max) max = min + 1;
          const step0 = (max - min) / n, mag = Math.pow(10, Math.floor(Math.log10(step0))), norm = step0 / mag;
          const step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
          const lo = Math.floor(min / step + 1e-9) * step, hi = Math.ceil(max / step - 1e-9) * step, ticks = [];
          for (let v = lo; v <= hi + step / 2; v += step) ticks.push(+v.toPrecision(12));
          return { lo, hi, ticks };
        }
        const num = v => Math.abs(v) >= 1000 ? v.toLocaleString('en-US', { maximumFractionDigits: 1 }) : String(+v.toFixed(1));
        function chartHtml(c, f, family) {
          const th = T(), ink = '#' + th.ink, pal = (th.palette || ['1F4E79']).map(x => '#' + x), W = f.w, H = f.h;
          const color = i => pal[i % pal.length], circ = c.type === 'pie' || c.type === 'doughnut', cats = c.categories || [], series = c.series || [];
          const legendRight = circ, legend = series.length > 1 || circ;
          let top = c.title ? 38 : 12, bottom = 12 + (legend && !legendRight ? 26 : 0), right = 12 + (legendRight ? Math.min(170, W * 0.28) : 0), left = 12;
          let g = '';
          if (c.title) g += '<text x="' + (W / 2) + '" y="24" text-anchor="middle" font-size="16" font-weight="700" fill="' + ink + '">' + esc(c.title) + '</text>';
          if (legend) {
            const names = circ ? cats : series.map(s => s.name);
            if (legendRight) names.forEach((n, i) => { g += '<rect x="' + (W - right + 18) + '" y="' + (top + 20 + i * 24) + '" width="11" height="11" fill="' + color(i) + '"/><text x="' + (W - right + 36) + '" y="' + (top + 30 + i * 24) + '" font-size="13" fill="' + ink + '">' + esc(n) + '</text>'; });
            else { let x = W / 2 - names.reduce((a, n) => a + 30 + n.length * 12, 0) / 2; names.forEach((n, i) => { g += '<rect x="' + x + '" y="' + (H - 22) + '" width="11" height="11" fill="' + color(i) + '"/><text x="' + (x + 16) + '" y="' + (H - 12) + '" font-size="13" fill="' + ink + '">' + esc(n) + '</text>'; x += 30 + n.length * 12; }); }
          }
          if (circ) {
            const vals = (series[0] ? series[0].values : []).map(v => Math.max(v || 0, 0)), total = vals.reduce((a, b) => a + b, 0) || 1;
            const cx = left + (W - left - right) / 2, cy = top + (H - top - bottom) / 2, R = Math.max(10, Math.min(W - left - right, H - top - bottom) / 2 - (c.labels && c.type === 'pie' ? 22 : 4)), r0 = c.type === 'doughnut' ? R * 0.55 : 0;
            let a = -Math.PI / 2;
            vals.forEach((v, i) => {
              const da = v / total * Math.PI * 2, a1 = a + da, big = da > Math.PI ? 1 : 0, p = (r, ang) => [cx + r * Math.cos(ang), cy + r * Math.sin(ang)];
              const [x0, y0] = p(R, a), [x1, y1] = p(R, a1), [x2, y2] = p(r0, a1), [x3, y3] = p(r0, a);
              if (v > 0) {
                g += da >= Math.PI * 2 - 1e-6 ? '<circle cx="' + cx + '" cy="' + cy + '" r="' + R + '" fill="' + color(i) + '"/>' : '<path d="M' + x0 + ' ' + y0 + ' A' + R + ' ' + R + ' 0 ' + big + ' 1 ' + x1 + ' ' + y1 + (r0 ? ' L' + x2 + ' ' + y2 + ' A' + r0 + ' ' + r0 + ' 0 ' + big + ' 0 ' + x3 + ' ' + y3 : ' L' + cx + ' ' + cy) + ' Z" fill="' + color(i) + '" stroke="#fff" stroke-width="1.5"/>';
                if (c.labels) { const mid = a + da / 2, [lx, ly] = p(c.type === 'pie' ? R + 14 : (R + r0) / 2, mid); g += '<text x="' + lx + '" y="' + (ly + 4) + '" text-anchor="middle" font-size="12" font-weight="' + (c.type === 'pie' ? 400 : 700) + '" fill="' + (c.type === 'pie' ? ink : '#fff') + '">' + Math.round(v / total * 100) + '%</text>'; }
              }
              a = a1;
            });
          } else {
            const horizontal = c.type === 'bar', stacked = c.stacked && c.type !== 'line', n = cats.length;
            let lo = Infinity, hi = -Infinity;
            for (let i = 0; i < n; i++) {
              let pos = 0, neg = 0;
              series.forEach(s => { const v = s.values[i]; if (v == null) return; lo = Math.min(lo, v); hi = Math.max(hi, v); if (stacked) { if (v >= 0) pos += v; else neg += v; } });
              if (stacked) { hi = Math.max(hi, pos); lo = Math.min(lo, neg); }
            }
            if (!isFinite(lo)) { lo = 0; hi = 1; }
            const zeroBased = c.type !== 'line' ? lo >= 0 : (lo >= 0 && lo < hi * 5 / 6);
            const sc = niceScale(zeroBased ? Math.min(0, lo) : lo, Math.max(hi, zeroBased ? 0 : hi), 5);
            const longest = cats.reduce((a, s) => Math.max(a, String(s).length), 0);
            const px0 = left + (horizontal ? 8 + Math.min(longest * 13, W * 0.3) : 44), px1 = W - right, py0 = top + 4, py1 = H - bottom - (horizontal ? 22 : 22);
            const bandAxis = horizontal ? (py1 - py0) : (px1 - px0), band = bandAxis / Math.max(n, 1);
            const vpos = v => horizontal ? px0 + (v - sc.lo) / (sc.hi - sc.lo) * (px1 - px0) : py1 - (v - sc.lo) / (sc.hi - sc.lo) * (py1 - py0);
            sc.ticks.forEach(t => { const p = vpos(t); g += horizontal ? '<line x1="' + p + '" y1="' + py0 + '" x2="' + p + '" y2="' + py1 + '" stroke="#d9d9d9"/><text x="' + p + '" y="' + (py1 + 16) + '" text-anchor="middle" font-size="12" fill="' + ink + '">' + num(t) + '</text>' : '<line x1="' + px0 + '" y1="' + p + '" x2="' + px1 + '" y2="' + p + '" stroke="#d9d9d9"/><text x="' + (px0 - 6) + '" y="' + (p + 4) + '" text-anchor="end" font-size="12" fill="' + ink + '">' + num(t) + '</text>'; });
            cats.forEach((cat, i) => { const m = (i + 0.5) * band; g += horizontal ? '<text x="' + (px0 - 6) + '" y="' + (py1 - m + 4) + '" text-anchor="end" font-size="12" fill="' + ink + '">' + esc(cat) + '</text>' : '<text x="' + (px0 + m) + '" y="' + (py1 + 16) + '" text-anchor="middle" font-size="12" fill="' + ink + '">' + esc(cat) + '</text>'; });
            const base = vpos(Math.max(sc.lo, Math.min(0, sc.hi)));
            if (c.type === 'column' || c.type === 'bar') {
              const k = series.length, bw = stacked ? band * 0.55 : band * 0.7 / k;
              cats.forEach((cat, i) => {
                let pos = 0, neg = 0;
                series.forEach((s, j) => {
                  const v = s.values[i]; if (v == null) return;
                  let a, b;
                  if (stacked) { if (v >= 0) { a = pos; pos += v; b = pos; } else { a = neg; neg += v; b = neg; } } else { a = 0; b = v; }
                  const o = stacked ? (i + 0.5) * band - bw / 2 : (i + 0.5) * band - bw * k / 2 + j * bw;
                  const p1 = vpos(a), p2 = vpos(b), x = horizontal ? Math.min(p1, p2) : px0 + o, y = horizontal ? py1 - o - bw : Math.min(p1, p2), w = horizontal ? Math.abs(p2 - p1) : bw, h = horizontal ? bw : Math.abs(p2 - p1);
                  g += '<rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" fill="' + color(j) + '"/>';
                  if (c.labels) g += horizontal ? '<text x="' + (stacked ? x + w / 2 : Math.max(p1, p2) + 4) + '" y="' + (y + h / 2 + 4) + '" text-anchor="' + (stacked ? 'middle' : 'start') + '" font-size="12" fill="' + (stacked ? '#fff' : ink) + '">' + num(v) + '</text>' : '<text x="' + (x + w / 2) + '" y="' + (stacked ? y + h / 2 + 4 : y - 4) + '" text-anchor="middle" font-size="12" fill="' + (stacked ? '#fff' : ink) + '">' + num(v) + '</text>';
                });
              });
              g += horizontal ? '<line x1="' + base + '" y1="' + py0 + '" x2="' + base + '" y2="' + py1 + '" stroke="#a6a6a6"/>' : '<line x1="' + px0 + '" y1="' + base + '" x2="' + px1 + '" y2="' + base + '" stroke="#a6a6a6"/>';
            } else {
              const acc = new Array(n).fill(0);
              series.forEach((s, j) => {
                const pts = cats.map((cat, i) => { const v = s.values[i]; if (v == null) return null; const y = (c.type === 'area' && stacked) ? (acc[i] += v) : v; return [px0 + (i + (c.type === 'area' ? 0 : 0.5)) * (c.type === 'area' ? (px1 - px0) / Math.max(n - 1, 1) : band), vpos(y), v]; }).filter(Boolean);
                if (!pts.length) return;
                if (c.type === 'area') { const lowerAcc = stacked ? series.slice(0, j).reduce((a, s2) => a.map((q, i) => q + (s2.values[i] || 0)), new Array(n).fill(0)) : null; const back = stacked ? pts.map(p => p).reverse().map((p, k) => [p[0], vpos(lowerAcc[pts.length - 1 - k])]) : [[pts[pts.length - 1][0], base], [pts[0][0], base]]; g += '<polygon points="' + pts.concat(back).map(p => p[0] + ',' + p[1]).join(' ') + '" fill="' + color(j) + '" fill-opacity=".85"/>'; }
                else { g += '<polyline fill="none" stroke="' + color(j) + '" stroke-width="2.5" points="' + pts.map(p => p[0] + ',' + p[1]).join(' ') + '"/>' + pts.map(p => '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="3.5" fill="' + color(j) + '"/>' + (c.labels ? '<text x="' + p[0] + '" y="' + (p[1] - 8) + '" text-anchor="middle" font-size="12" fill="' + ink + '">' + num(p[2]) + '</text>' : '')).join(''); }
              });
              g += '<line x1="' + px0 + '" y1="' + py1 + '" x2="' + px1 + '" y2="' + py1 + '" stroke="#a6a6a6"/>';
            }
          }
          return '<svg class="abs" style="left:' + f.x + 'px;top:' + f.y + 'px" width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '" font-family="' + esc(family) + '">' + g + '</svg>';
        }
        function slideHtml(s, edit) {
          edit = !!edit && EDIT && s.isComplete !== false;
          const theme = T(), layout = layoutOf(s);
          const badge = s.isComplete === false ? '<div class="badge">' + esc(L.writing) + '</div>' : '';
          const t = role(layout, 'title');
          const family = (role(theme.content, 'body') || t || { look: { font: 'sans-serif' } }).look.font;
          let html = '<div class="slide" style="width:' + theme.width + 'px;height:' + theme.height + 'px;font-family:' + family + ';--mark:#' + theme.accent + '">' + shapesHtml(layout, s);
          if (t) html += '<div class="ph" style="' + frame(t.frame) + look(Object.assign({}, t.look, { size: Math.min(t.look.size, s.titlePt || t.look.size) })) + '"><span' + ed(edit, 'data-t="title"') + '>' + esc(s.title) + '</span></div>';

          if (s.kind !== 'content') {
            // the subtitle goes under the title, the lines after it (who, when) below that
            const items = [];
            if (s.subtitle || (edit)) items.push({ t: 'subtitle', text: esc(s.subtitle || '') });
            if (s.kind === 'title') (s.blocks || []).forEach((b, i) => items.push({ t: 'block', i, text: runs(b.runs) }));
            const part = it => '<span' + ed(edit, it.t === 'subtitle' ? 'data-t="subtitle"' : 'data-t="block" data-k="p" data-b="' + it.i + '"') + '>' + it.text + '</span>';
            const first = role(layout, 'sub1'), second = role(layout, 'sub2');
            // two places for lines: the subtitle in the first, the rest in the second; one place: all of them in it
            const firstItems = second ? items.slice(0, 1) : items;
            if (first && items.length > 0) html += '<div class="ph" style="' + frame(first.frame) + look(Object.assign({}, first.look, { size: Math.min(first.look.size, s.sub1Pt || first.look.size) })) + 'flex-direction:column"><div>' + firstItems.map(part).join('</div><div>') + '</div></div>';
            if (second && items.length > 1) html += '<div class="ph" style="' + frame(second.frame) + look(Object.assign({}, second.look, { size: Math.min(second.look.size, s.sub2Pt || second.look.size) })) + 'flex-direction:column"><div>' + items.slice(1).map(part).join('</div><div>') + '</div></div>';
            return html + badge + '</div>';
          }

          const bodyPh = role(layout, 'body'), f0 = bodyPh ? bodyPh.frame : theme.contentFrame, f = Object.assign({}, f0, { w: s.textWidth || f0.w }), body = s.bodyPt || 24, counters = [];
          let inner = '';
          (s.blocks || []).forEach((b, i) => {
            if (b.t === 'b') {
              const lvl = b.level || 0, size = lvl === 0 ? body : body * 0.9;
              counters.length = lvl + 1;
              let mark = lvl === 0 ? '•' : '–';
              if (b.numbered) { counters[lvl] = (counters[lvl] || 0) + 1; mark = counters[lvl] + '.'; } else counters[lvl] = 0;
              inner += '<div class="li' + (b.numbered ? ' num' : '') + '" style="font-size:' + size + 'px;padding-left:' + (28 * (lvl + 1)) + 'px"><span class="mk" style="left:' + (28 * lvl) + 'px">' + mark + '</span><span' + ed(edit, 'data-t="block" data-k="b" data-b="' + i + '"') + '>' + runs(b.runs) + '</span></div>';
            } else if (b.t === 'chart') {
              counters.length = 0;
            } else if (b.t === 'table') {
              counters.length = 0;
              inner += '<table style="font-size:' + (s.tablePt || 14) + 'px">' + (b.rows || []).map((r, ri) => '<tr>' + r.map((c, ci) => { const h = b.hasHeader && ri === 0, tag = h ? 'th' : 'td'; return '<' + tag + '><span' + ed(edit, 'data-t="cell" data-b="' + i + '" data-r="' + ri + '" data-c="' + ci + '"') + '>' + runs(c) + '</span></' + tag + '>'; }).join('') + '</tr>').join('') + '</table>';
            } else {
              counters.length = 0;
              inner += '<div class="p" style="font-size:' + body + 'px"><span' + ed(edit, 'data-t="block" data-k="p" data-b="' + i + '"') + '>' + runs(b.runs) + '</span></div>';
            }
          });
          const chart = (s.blocks || []).find(b => b.t === 'chart');
          return html + '<div class="body" style="' + frame(f) + (bodyPh ? 'color:#' + bodyPh.look.color : '') + '">' + inner + '</div>' + (chart && s.chartFrame ? chartHtml(chart, s.chartFrame, family) : '') + badge + '</div>';
        }

        function render() {
          const slides = deck.slides || [];
          if (slides.length === 0) { $('fit').innerHTML = '<div id="empty">' + esc(L.empty) + '</div>'; $('fit').style.cssText = ''; $('strip').innerHTML = ''; $('count').textContent = ''; return; }
          if (follow) current = slides.length - 1;
          current = Math.max(0, Math.min(current, slides.length - 1));
          const ts = 160 / T().width, th = Math.round(T().height * ts);
          $('fit').innerHTML = slideHtml(slides[current], true);
          $('fit').querySelectorAll('[data-e]').forEach(el => { el.dataset.orig = toMd(el); });
          $('strip').innerHTML = slides.map((s, i) => '<div class="thumb' + (i === current ? ' sel' : '') + (s.isComplete === false ? ' writing' : '') + '" data-i="' + i + '" style="height:' + th + 'px">' + slideHtml(s, false).replace('<div class="slide"', '<div class="slide" data-thumb') + '<span class="n">' + s.number + '</span></div>').join('');
          $('strip').querySelectorAll('.slide').forEach(el => { el.style.transform = 'scale(' + ts + ')'; });
          $('count').textContent = (current + 1) + ' / ' + slides.length;
          renderNotes();
          $('delBtn').disabled = slides.length <= 1; $('leftBtn').disabled = current === 0; $('rightBtn').disabled = current >= slides.length - 1;
          fit();
          const sel = $('strip').querySelector('.sel'); if (sel) sel.scrollIntoView({ block: 'nearest', inline: 'nearest' });
          if (pendingFocus) { focusBlock(pendingFocus.block); pendingFocus = null; }
        }

        function renderNotes() {
          const n = deck.slides[current].notes, box = $('notes');
          if (EDIT && deck.slides[current].isComplete !== false) {
            box.innerHTML = '<b>' + esc(L.notes) + '</b><textarea id="notesEdit" placeholder="' + esc(L.notesHint) + '"></textarea>';
            const t = $('notesEdit'); t.value = n || '';
            t.onblur = () => { const v = t.value.trim(); if (v !== (deck.slides[current].notes || '').trim()) edit({ op: 'text', slide: current + 1, target: 'notes', md: v }); };
            box.classList.toggle('on', showNotes);
          } else {
            box.innerHTML = n ? '<b>' + esc(L.notes) + '</b>' + esc(n) : '';
            box.classList.toggle('on', showNotes && !!n);
          }
        }

        function fit() {
          const box = $('stage').getBoundingClientRect(), pad = document.body.classList.contains('present') ? 0 : 26;
          const W = T().width, H = T().height, s = Math.min((box.width - pad) / W, (box.height - pad) / H);
          if (!(s > 0)) return;
          const f = $('fit'), slide = f.firstElementChild;
          f.style.width = (W * s) + 'px'; f.style.height = (H * s) + 'px';
          if (slide && slide.classList.contains('slide')) { slide.style.transformOrigin = '0 0'; slide.style.transform = 'scale(' + s + ')'; }
        }

        // ---- editing ----
        function focusBlock(b) {
          const el = $('fit').querySelector('[data-t="block"][data-b="' + b + '"]'); if (!el) return;
          el.focus(); const r = document.createRange(); r.selectNodeContents(el); r.collapse(false); const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r);
        }
        // a text change: the page's own copy of the deck follows at once (the app keeps the real one and is told)
        function edit(op) {
          const s = deck.slides[current];
          if (op.op === 'text') {
            if (op.target === 'title') s.title = op.md;
            else if (op.target === 'subtitle') s.subtitle = op.md || null;
            else if (op.target === 'notes') s.notes = op.md || null;
            else if (op.target === 'block') s.blocks[op.block].runs = parseRuns(op.md);
            else if (op.target === 'cell') s.blocks[op.block].rows[op.row][op.col] = parseRuns(op.md);
          }
          post(op);
        }
        function commit(el) {
          if (!el.isConnected) return; // the page was redrawn under it
          const md = toMd(el).replace(/\s+/g, ' ').trim();
          const t = el.dataset.t, op = { op: 'text', slide: current + 1, target: t, md };
          // a list line left empty is a line nobody wants: it goes
          if (t === 'block' && el.dataset.k === 'b' && md === '') { post({ op: 'removeBlock', slide: current + 1, block: +el.dataset.b }); return; }
          if (md === (el.dataset.orig || '')) return;
          if (t === 'block' || t === 'cell') op.block = +el.dataset.b;
          if (t === 'cell') { op.row = +el.dataset.r; op.col = +el.dataset.c; }
          el.dataset.orig = md;
          edit(op);
        }
        $('fit').addEventListener('focusout', e => { const el = e.target.closest && e.target.closest('[data-e]'); if (el) commit(el); });
        $('fit').addEventListener('paste', e => {
          const el = e.target.closest && e.target.closest('[data-e]'); if (!el) return;
          e.preventDefault(); document.execCommand('insertText', false, (e.clipboardData.getData('text/plain') || '').replace(/\s+/g, ' '));
        });
        $('fit').addEventListener('keydown', e => {
          const el = e.target.closest && e.target.closest('[data-e]'); if (!el) return;
          e.stopPropagation();
          const bullet = el.dataset.t === 'block' && el.dataset.k === 'b', b = +el.dataset.b;
          if (e.key === 'Enter') {
            e.preventDefault();
            // Enter ends the line and starts a new one; on an empty line it just ends the list (the empty line goes)
            if (bullet && toMd(el).trim() !== '') { commit(el); pendingFocus = { block: b + 1 }; post({ op: 'insertAfter', slide: current + 1, block: b }); } else el.blur();
          } else if (e.key === 'Escape') {
            e.preventDefault(); el.innerHTML = runs(parseRuns(el.dataset.orig || '')); el.blur();
          } else if (e.key === 'Backspace' && bullet && el.textContent === '') {
            e.preventDefault(); pendingFocus = b > 0 ? { block: b - 1 } : null; post({ op: 'removeBlock', slide: current + 1, block: b });
          }
        });

        $('addBtn').onclick = () => { const at = current + 1; follow = false; post({ op: 'addSlide', slide: at, title: L.newSlide }); current = at; };
        $('dupBtn').onclick = () => { const at = current + 1; follow = false; post({ op: 'duplicateSlide', slide: at }); current = at; };
        $('delBtn').onclick = () => { if (deck.slides.length > 1) { const at = current + 1; follow = false; post({ op: 'deleteSlide', slide: at }); current = Math.min(current, deck.slides.length - 2); } };
        $('leftBtn').onclick = () => { if (current > 0) { const at = current + 1; post({ op: 'moveSlide', slide: at, delta: -1 }); current -= 1; } };
        $('rightBtn').onclick = () => { if (current < deck.slides.length - 1) { const at = current + 1; post({ op: 'moveSlide', slide: at, delta: 1 }); current += 1; } };

        function go(i) { follow = false; current = i; render(); }
        window.setDeck = d => { deck = d; render(); };
        window.addEventListener('resize', fit);
        $('strip').addEventListener('click', e => { const t = e.target.closest('.thumb'); if (t) go(+t.dataset.i); });
        $('prev').onclick = () => go(current - 1);
        $('next').onclick = () => go(current + 1);
        $('notesBtn').onclick = () => { showNotes = !showNotes; $('notesBtn').setAttribute('aria-pressed', showNotes); renderNotes(); };
        $('presentBtn').onclick = () => { document.body.classList.toggle('present'); if (document.body.classList.contains('present') && document.documentElement.requestFullscreen) document.documentElement.requestFullscreen().catch(() => {}); fit(); };
        document.addEventListener('fullscreenchange', () => { if (!document.fullscreenElement) { document.body.classList.remove('present'); fit(); } });
        document.addEventListener('keydown', e => {
          if (e.target.closest && e.target.closest('[data-e],textarea')) return;
          const k = e.key;
          if (k === 'ArrowRight' || k === 'ArrowDown' || k === 'PageDown' || k === ' ') go(current + 1);
          else if (k === 'ArrowLeft' || k === 'ArrowUp' || k === 'PageUp') go(current - 1);
          else if (k === 'Home') go(0);
          else if (k === 'End') go((deck.slides || []).length - 1);
          else if (k === 'Escape') { document.body.classList.remove('present'); fit(); }
          else return;
          e.preventDefault();
        });
        $('prev').title = $('prev').ariaLabel = L.previous; $('next').title = $('next').ariaLabel = L.next;
        for (const [id, label] of [['addBtn', L.addSlide], ['dupBtn', L.duplicate], ['delBtn', L.deleteSlide], ['leftBtn', L.moveLeft], ['rightBtn', L.moveRight]]) { $(id).title = label; $(id).setAttribute('aria-label', label); }
        $('hint').textContent = L.editHint;
        document.querySelectorAll('.editonly').forEach(el => { if (!EDIT) el.style.display = 'none'; });
        $('notesBtn').textContent = L.notes; $('presentBtn').textContent = '▶ ' + L.present;
        render();
        </script></body></html>
        """;
}
