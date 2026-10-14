// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>
/// The page of the memory window. Three views of the same memories: a list (the default, like other assistants' "manage
/// memory"), a timeline (when, and in which conversation, something was remembered) and a connection graph (what belongs
/// together). A panel on the right shows the selected memory or conversation; a search covers memories and past
/// conversations; tidying up (consolidation) is a review of the model's suggestions.
/// It has no texts of its own: C# sends them (and the data) in an "init" message, and hears back through web messages.
/// Messages to C#: ready, search, save, create, forget, restore, revive, openConversation, estimate, consolidate, cancelConsolidate, apply.
/// Messages from C#: init, data, focus, search, toast, estimate, plan, applied, error.
/// </summary>
internal static class MemoryPage
{
    public static string Html(string nonce) => Template.Replace("__NONCE__", nonce);

    private const string Template = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-__NONCE__'; script-src 'nonce-__NONCE__';">
        <style nonce="__NONCE__">
        :root {
            color-scheme: light dark;
            --bg: #fbfbfc; --panel: #ffffff; --line: #e1e4e8; --text: #1f2328; --muted: #656d76; --accent: #0969da;
            --edge: #b4bcc6; --related: #8250df; --similar: #d4a72c; --dim: 0.3; --hl: #fff8c5;
            --preference: #7c5cff; --project: #1f9d8b; --decision: #e8892b; --fact: #3b82c4; --session: #8b95a1;
        }
        @media (prefers-color-scheme: dark) {
            :root { --bg: #1b1d21; --panel: #24272c; --line: #3a3f46; --text: #e6e8eb; --muted: #9aa3ad; --accent: #58a6ff; --edge: #5b636d; --session: #7b8794; --hl: #4a4420; }
        }
        * { box-sizing: border-box; }
        html, body { height: 100%; margin: 0; }
        header, #stage, #banner, button, .chip { user-select: none; -webkit-user-select: none; }
        aside, #dialog, textarea, input { user-select: text; -webkit-user-select: text; }
        body { background: var(--bg); color: var(--text); font: 14px "Segoe UI Variable Text", "Segoe UI", "Microsoft JhengHei UI", sans-serif; display: flex; flex-direction: column; overflow: hidden; }
        header { padding: 10px 14px 8px; border-bottom: 1px solid var(--line); background: var(--panel); display: flex; flex-direction: column; gap: 8px; }
        .bar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
        header h1 { font-size: 15px; margin: 0 8px 0 0; font-weight: 600; }
        #intro { color: var(--muted); font-size: 12.5px; }
        input[type=search], textarea, select { background: var(--bg); color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 6px 9px; font: inherit; }
        input[type=search] { width: 320px; max-width: 40vw; }
        button { background: var(--bg); color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 6px 12px; font: inherit; cursor: pointer; }
        button:hover { border-color: var(--accent); }
        button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
        button.danger:hover { border-color: #cf222e; color: #cf222e; }
        button.small { padding: 2px 9px; font-size: 12.5px; }
        .seg { display: inline-flex; border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
        .seg button { border: 0; border-radius: 0; background: transparent; }
        .seg button.on { background: var(--accent); color: #fff; }
        .chips { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; color: var(--muted); }
        .chips .lab { font-size: 12px; margin-right: 2px; }
        .chips .lab.gap { margin-left: 10px; }
        .chip { display: inline-flex; gap: 6px; align-items: center; padding: 2px 9px; border: 1px solid var(--line); border-radius: 99px; cursor: pointer; background: var(--panel); color: var(--text); font-size: 13px; }
        .chip.off { opacity: 0.4; }
        .dot { width: 9px; height: 9px; border-radius: 50%; display: inline-block; flex: none; }
        .dot.preference { background: var(--preference); } .dot.project { background: var(--project); } .dot.decision { background: var(--decision); } .dot.fact { background: var(--fact); }
        .spacer { flex: 1; }
        #banner { display: none; padding: 8px 14px; background: color-mix(in srgb, var(--accent) 12%, transparent); border-bottom: 1px solid var(--line); }
        #banner.show { display: flex; gap: 12px; align-items: center; }
        main { flex: 1; display: flex; min-height: 0; position: relative; }
        #stage { flex: 1; position: relative; min-width: 0; display: flex; flex-direction: column; }
        .pane { flex: 1; min-height: 0; position: relative; display: none; }
        .pane.show { display: block; }
        .scroll { overflow: auto; padding: 14px 18px 40px; }
        svg#svg { width: 100%; height: 100%; display: block; cursor: grab; touch-action: none; }
        svg#svg.panning { cursor: grabbing; }
        #hint { position: absolute; left: 12px; bottom: 10px; color: var(--muted); font-size: 12px; pointer-events: none; }
        #graphNote { position: absolute; left: 50%; top: 14px; transform: translateX(-50%); background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 6px 12px; color: var(--muted); font-size: 12.5px; display: none; max-width: 70%; }
        #graphNote.show { display: block; }
        #legend { position: absolute; right: 12px; bottom: 10px; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; font-size: 12px; color: var(--muted); display: grid; grid-template-columns: auto auto; gap: 4px 14px; }
        #legend div { display: flex; align-items: center; gap: 6px; }
        #focusbar { position: absolute; right: 12px; top: 10px; background: var(--panel); border: 1px solid var(--line); border-radius: 99px; padding: 3px 11px; font-size: 12.5px; cursor: pointer; user-select: none; }
        #focusbar.off { opacity: .55; }
        #empty { color: var(--muted); text-align: center; padding: 60px 30px; display: none; line-height: 1.8; }
        #empty.show { display: block; }
        aside { width: 340px; border-left: 1px solid var(--line); background: var(--panel); overflow: auto; padding: 14px; display: none; flex-direction: column; gap: 10px; flex: none; }
        aside.show { display: flex; }
        aside h2 { font-size: 14px; margin: 0; }
        aside label { display: block; color: var(--muted); font-size: 12px; margin-bottom: 3px; }
        aside textarea { width: 100%; min-height: 110px; resize: vertical; }
        aside select { width: 100%; }
        .help { color: var(--muted); font-size: 12px; }
        .meta { color: var(--muted); font-size: 12px; line-height: 1.7; }
        .row { display: flex; gap: 8px; flex-wrap: wrap; }
        .link { color: var(--accent); cursor: pointer; background: none; border: 0; padding: 0; text-align: left; font: inherit; }
        .link:hover { text-decoration: underline; }
        .list { display: flex; flex-direction: column; gap: 6px; }
        .item { padding: 7px 9px; border: 1px solid var(--line); border-radius: 6px; cursor: pointer; background: var(--bg); }
        .item:hover { border-color: var(--accent); }
        .item small { color: var(--muted); display: block; }
        .warn { color: var(--muted); font-style: italic; }
        .mem { display: flex; gap: 10px; align-items: flex-start; padding: 10px 12px; border: 1px solid var(--line); border-radius: 8px; background: var(--panel); margin-bottom: 8px; cursor: pointer; }
        .mem:hover { border-color: var(--accent); }
        .mem.sel { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent); }
        .mem.hl { background: var(--hl); }
        .mem.archived .txt { opacity: .6; }
        .mem .dot { margin-top: 6px; }
        .mem .main { flex: 1; min-width: 0; }
        .mem .txt { line-height: 1.5; overflow-wrap: anywhere; }
        .mem .sub { color: var(--muted); font-size: 12px; margin-top: 3px; display: flex; flex-wrap: wrap; gap: 4px 10px; align-items: center; }
        .badge { font-size: 11.5px; padding: 0 8px; border-radius: 99px; border: 1px solid var(--line); }
        .badge.new { border-color: var(--decision); color: var(--decision); }
        .badge.archived { border-style: dashed; }
        .mem .actions { display: flex; gap: 6px; visibility: hidden; flex: none; }
        .mem:hover .actions, .mem.sel .actions { visibility: visible; }
        .day { font-size: 12.5px; color: var(--muted); font-weight: 600; margin: 18px 0 8px; position: sticky; top: -14px; background: var(--bg); padding: 4px 0; z-index: 1; }
        .day:first-child { margin-top: 0; }
        .conv { border-left: 3px solid var(--session); padding: 4px 0 4px 12px; margin: 0 0 14px 4px; }
        .conv.manual { border-left-color: var(--accent); }
        .conv .ct { font-weight: 600; margin-bottom: 6px; }
        .conv .ct small { color: var(--muted); font-weight: 400; margin-left: 8px; }
        .conv .mem { margin-bottom: 6px; }
        #results { position: absolute; left: 12px; top: 10px; width: 380px; max-height: calc(100% - 40px); overflow: auto; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 10px; display: none; flex-direction: column; gap: 8px; box-shadow: 0 6px 24px rgba(0,0,0,.18); z-index: 3; }
        #results.show { display: flex; }
        #results h3 { margin: 4px 0 0; font-size: 12px; color: var(--muted); font-weight: 600; }
        #toast { position: absolute; left: 50%; bottom: 18px; transform: translateX(-50%); background: var(--text); color: var(--bg); padding: 8px 14px; border-radius: 8px; display: none; gap: 12px; align-items: center; z-index: 6; }
        #toast.show { display: flex; }
        #toast button { background: transparent; color: inherit; border-color: currentColor; padding: 2px 8px; }
        #overlay { position: absolute; inset: 0; background: rgba(0,0,0,.45); display: none; align-items: center; justify-content: center; padding: 24px; z-index: 5; }
        #overlay.show { display: flex; }
        #dialog { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 18px; width: min(720px, 100%); max-height: 100%; overflow: auto; display: flex; flex-direction: column; gap: 12px; }
        #dialog h2 { margin: 0; font-size: 16px; }
        .op { border: 1px solid var(--line); border-radius: 8px; padding: 10px; display: flex; gap: 10px; align-items: flex-start; }
        .op input { margin-top: 3px; }
        .op .tag { font-size: 11px; padding: 1px 7px; border-radius: 99px; background: var(--accent); color: #fff; margin-right: 6px; }
        .op .old { color: var(--muted); text-decoration: line-through; display: block; }
        .op .reason { color: var(--muted); font-size: 12px; display: block; margin-top: 3px; }
        #tip { position: absolute; display: none; max-width: 300px; padding: 7px 10px; background: var(--panel); color: var(--text); border: 1px solid var(--line); border-radius: 8px; font-size: 12px; line-height: 1.5; box-shadow: 0 4px 18px rgba(0,0,0,.18); pointer-events: none; z-index: 4; transform: translate(-50%, -100%); }
        #tip.show { display: block; }
        #tip small { display: block; color: var(--muted); margin-top: 2px; }
        .node text { font-size: 10.5px; fill: var(--text); pointer-events: none; paint-order: stroke; stroke: var(--bg); stroke-width: 3px; }
        .node { cursor: pointer; }
        .node.dim, .edge.dim { opacity: var(--dim); }
        .node .shape.t-preference { fill: var(--preference); } .node .shape.t-project { fill: var(--project); } .node .shape.t-decision { fill: var(--decision); } .node .shape.t-fact { fill: var(--fact); }
        .node .shape.s { fill: var(--session); }
        .node .ring { fill: none; stroke: var(--text); stroke-width: 0; }
        .node.sel .ring { stroke: var(--text); stroke-width: 3; }
        .node.current .ring { stroke: var(--accent); stroke-width: 2; stroke-dasharray: 4 3; }
        .node.current.sel .ring { stroke: var(--text); stroke-dasharray: none; }
        .node.new .shape { stroke: var(--text); stroke-width: 1.5; stroke-dasharray: 3 2; }
        .node.settled .shape { stroke: var(--text); stroke-width: 1.5; }
        .node.archived { opacity: 0.45; }
        .node.hit .ring { stroke: #cf222e; stroke-width: 3; }
        .edge { stroke: var(--edge); stroke-width: 1.2; fill: none; }
        .edge.related { stroke: var(--related); stroke-dasharray: 5 3; stroke-width: 1.6; }
        .edge.similar { stroke: var(--similar); stroke-dasharray: 2 4; stroke-width: 1.6; }
        </style>
        </head>
        <body>
        <header>
            <div class="bar">
                <h1 id="t-title"></h1>
                <input id="q" type="search" autocomplete="off">
                <div class="seg" id="views"><button id="v-list" class="on"></button><button id="v-timeline"></button><button id="v-graph"></button></div>
                <span class="spacer"></span>
                <button id="b-add"></button>
                <button id="b-consolidate" class="primary"></button>
            </div>
            <div class="bar"><div class="chips" id="filters"></div></div>
            <div id="intro"></div>
        </header>
        <div id="banner"><span id="banner-text"></span><button id="banner-go" class="primary"></button></div>
        <main>
            <div id="stage">
                <div id="pane-list" class="pane show scroll"><div id="list"></div><div id="empty"></div></div>
                <div id="pane-timeline" class="pane scroll"><div id="timeline"></div></div>
                <div id="pane-graph" class="pane">
                    <svg id="svg" xmlns="http://www.w3.org/2000/svg"><g id="vp"><g id="edges"></g><g id="nodes"></g></g></svg>
                    <div id="graphNote"></div>
                    <div id="focusbar"></div>
                    <div id="legend"></div>
                    <div id="hint"></div>
                    <div id="tip"></div>
                </div>
                <div id="results"></div>
                <div id="toast"><span id="toast-text"></span><button id="toast-undo"></button></div>
            </div>
            <aside id="side"></aside>
            <div id="overlay"><div id="dialog"></div></div>
        </main>
        <script nonce="__NONCE__">
        'use strict';
        const post = m => window.chrome.webview.postMessage(m);
        const $ = id => document.getElementById(id);
        const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
        const SVGNS = 'http://www.w3.org/2000/svg';
        let T = {}, data = { memories: [], sessions: [], edges: [] }, enabled = true, remind = 0;
        let view = 'list', selected = null, hits = null, highlight = new Set();
        let nodes = [], byId = new Map(), links = [], adj = new Map(), fullAdj = new Map();
        const pos = new Map();   // where every node was last put, so the graph looks the same each time
        const filt = { types: { preference: true, project: true, decision: true, fact: true }, states: { new: true, settled: true, archived: true } };
        const vp = { x: 0, y: 0, k: 1 };
        let hover = null, lastForget = null, userMoved = false, focusOnly = true, editing = false, hlTimer = 0;

        const fmt = s => s ? new Date(s).toLocaleString([], { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) : '–';
        const fmtDay = s => s ? new Date(s).toLocaleDateString([], { year: 'numeric', month: '2-digit', day: '2-digit' }) : '–';
        const fmtTime = s => s ? new Date(s).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
        const cut = (s, n) => { s = String(s || '').replace(/\s+/g, ' '); return s.length > n ? s.slice(0, n) + '…' : s; };
        const hash = s => { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return h; };
        const rnd = seed => { const x = Math.sin(seed) * 10000; return x - Math.floor(x); };
        const sessionOf = id => data.sessions.find(s => s.id === id);

        // ---------- messages from C#
        window.chrome.webview.addEventListener('message', e => {
            const m = e.data;
            switch (m.type) {
                case 'init': T = m.strings; enabled = m.enabled; remind = m.remind; data = m.data; initUi(); renderAll(true); if (m.focus && m.focus.length) focusOn(m.focus); break;
                case 'data': data = m.data; remind = m.remind; renderAll(!userMoved); renderBanner(); break;
                case 'focus': focusOn(m.ids); break;
                case 'search': showResults(m); break;
                case 'toast': toast(m.text, m.undoId); break;
                case 'estimate': showEstimate(m); break;
                case 'plan': showPlan(m); break;
                case 'applied': closeOverlay(); clearHighlight(false); toast(T.applied.replace('{0}', m.count)); break;
                case 'error': showError(m.text); break;
            }
        });

        function initUi() {
            document.title = T.title;
            $('t-title').textContent = T.title;
            $('intro').textContent = T.intro;
            $('q').placeholder = T.search;
            $('v-list').textContent = T.viewList; $('v-timeline').textContent = T.viewTimeline; $('v-graph').textContent = T.viewGraph;
            $('b-add').textContent = T.add; $('b-consolidate').textContent = T.consolidate; $('b-consolidate').title = T.consolidateTip;
            $('hint').textContent = T.hint;
            $('toast-undo').textContent = T.undo;
            const chips = $('filters'); chips.replaceChildren();
            const lab = document.createElement('span'); lab.className = 'lab'; lab.textContent = T.types; chips.append(lab);
            for (const t of ['preference', 'project', 'decision', 'fact']) chips.append(chip(t, 'types', `<span class="dot ${t}"></span>${esc(T[t])}`, T[t + 'Help']));
            const lab2 = document.createElement('span'); lab2.className = 'lab gap'; lab2.textContent = T.states; chips.append(lab2);
            for (const s of ['new', 'settled', 'archived']) chips.append(chip(s, 'states', esc(T[s]), T[s + 'Help']));
            $('legend').innerHTML =
                `<div><svg width="16" height="16"><circle cx="8" cy="8" r="6" fill="#7c5cff"/></svg>${esc(T.legendMemory)}</div>` +
                `<div><svg width="22" height="8"><line x1="0" y1="4" x2="22" y2="4" stroke="#b4bcc6" stroke-width="2"/></svg>${esc(T.legendSource)}</div>` +
                `<div><svg width="16" height="16"><rect x="2" y="2" width="12" height="12" rx="3" fill="#8b95a1"/></svg>${esc(T.legendConversation)}</div>` +
                `<div><svg width="22" height="8"><line x1="0" y1="4" x2="22" y2="4" stroke="#8250df" stroke-width="2" stroke-dasharray="5 3"/></svg>${esc(T.legendRelated)}</div>` +
                `<div><svg width="16" height="16"><rect x="1.5" y="1.5" width="13" height="13" rx="4" fill="none" stroke="#0969da" stroke-width="2" stroke-dasharray="4 3"/></svg>${esc(T.legendCurrent)}</div>` +
                `<div><svg width="22" height="8"><line x1="0" y1="4" x2="22" y2="4" stroke="#d4a72c" stroke-width="2" stroke-dasharray="2 4"/></svg>${esc(T.legendSimilar)}</div>`;
            renderBanner();
        }
        function chip(key, group, html, help) {
            const c = document.createElement('span'); c.className = 'chip'; c.innerHTML = html; if (help) c.title = help;
            c.onclick = () => { clearHighlight(false); filt[group][key] = !filt[group][key]; c.classList.toggle('off', !filt[group][key]); renderAll(false); };
            return c;
        }
        function renderBanner() {
            const n = data.memories.filter(m => m.status === 'new').length;
            const show = (!enabled) || (remind > 0 && n >= remind);
            $('banner').classList.toggle('show', show);
            $('banner-text').textContent = !enabled ? T.off : T.reminder.replace('{0}', n);
            $('banner-go').style.display = enabled ? '' : 'none';
            $('banner-go').textContent = T.consolidate;
        }
        const visibleMemory = m => filt.types[m.type] !== false && filt.states[m.status] !== false;

        // ---------- the three views
        function setView(v) {
            view = v;
            clearHighlight(false);
            for (const [id, name] of [['v-list', 'list'], ['v-timeline', 'timeline'], ['v-graph', 'graph']]) $(id).classList.toggle('on', name === v);
            for (const name of ['list', 'timeline', 'graph']) $('pane-' + name).classList.toggle('show', name === v);
            hideTip();
            renderAll(true);
        }
        $('v-list').onclick = () => setView('list'); $('v-timeline').onclick = () => setView('timeline'); $('v-graph').onclick = () => setView('graph');

        function clearHighlight(redraw) {
            clearTimeout(hlTimer);
            if (!highlight.size) return;
            highlight = new Set();
            if (redraw) renderAll(false);
        }
        function renderAll(fit) {
            if (view === 'list') renderList();
            else if (view === 'timeline') renderTimeline();
            else rebuild(fit);
            renderSide();
        }

        // list ------------------------------------------------------------
        function sourceLine(m) {
            if (m.sources.length) {
                const s = sessionOf(m.sources[0].conversationId);
                const more = m.sources.length > 1 ? ` +${m.sources.length - 1}` : '';
                if (!s) return `${esc(T.from)} ${esc(T.sourceGone)}${more}`;
                return `${esc(T.from)} <button class="link" data-open="${esc(m.sources[0].conversationId)}" data-seq="${m.sources[0].seq}">${esc(cut(s.title, 40))}</button>${more}`;
            }
            return m.origin === 'user' ? esc(T.manual) : '';
        }
        function usage(m) {
            return m.uses > 0 ? `${esc(T.usedTimes.replace('{0}', m.uses))} · ${esc(T.lastUsed)} ${esc(fmtDay(m.lastUsed))}` : esc(T.neverUsed);
        }
        function memHtml(m, withSource) {
            const sel = selected && selected.id === m.id ? ' sel' : '';
            const hl = highlight.has(m.id) ? ' hl' : '';
            return `<div class="mem ${m.status}${sel}${hl}" data-sel="${esc(m.id)}">
                <span class="dot ${m.type}" title="${esc(T[m.type])}"></span>
                <div class="main"><div class="txt">${esc(m.text)}</div>
                    <div class="sub"><span>${esc(T[m.type])}</span>${m.status === 'settled' ? '' : `<span class="badge ${m.status}" title="${esc(T[m.status + 'Help'])}">${esc(T[m.status])}</span>`}
                    <span>${usage(m)}</span>${withSource ? `<span>${sourceLine(m)}</span>` : ''}${m.scope === 'sandbox' ? `<span class="badge">${esc(T.sandbox)}</span>` : ''}</div></div>
                <div class="actions"><button class="small">${esc(T.edit)}</button><button class="small danger" data-forget="${esc(m.id)}">${esc(T.forget)}</button></div>
            </div>`;
        }
        function wireRows(root) {
            root.querySelectorAll('.mem').forEach(el => el.addEventListener('click', ev => {
                if (ev.target.closest('[data-open],[data-forget]')) return;
                const m = data.memories.find(x => x.id === el.dataset.sel); if (m) { editing = false; selected = { kind: 'm', id: m.id }; highlight.clear(); renderAll(false); }
            }));
            root.querySelectorAll('[data-forget]').forEach(el => el.onclick = ev => { ev.stopPropagation(); if (selected && selected.id === el.dataset.forget) selected = null; post({ type: 'forget', id: el.dataset.forget }); });
            root.querySelectorAll('[data-open]').forEach(el => el.onclick = ev => { ev.stopPropagation(); post({ type: 'openConversation', id: el.dataset.open, seq: +el.dataset.seq || 0 }); });
        }
        function renderList() {
            const mems = data.memories.filter(visibleMemory);
            $('list').innerHTML = mems.map(m => memHtml(m, true)).join('');
            $('empty').classList.toggle('show', mems.length === 0);
            $('empty').innerHTML = data.memories.length === 0
                ? `${esc(T.empty)}<br><button class="primary" id="empty-add">${esc(T.add)}</button>`
                : esc(T.emptyFiltered);
            if ($('empty-add')) $('empty-add').onclick = openAdd;
            wireRows($('list'));
            if (highlight.size) { const first = $('list').querySelector('.mem.hl'); if (first) first.scrollIntoView({ block: 'center' }); }
        }

        // timeline --------------------------------------------------------
        function dayLabel(d) {
            const day = new Date(d); day.setHours(0, 0, 0, 0);
            const today = new Date(); today.setHours(0, 0, 0, 0);
            const diff = Math.round((today - day) / 86400000);
            const base = day.toLocaleDateString([], { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' });
            return diff === 0 ? `${T.today} · ${base}` : diff === 1 ? `${T.yesterday} · ${base}` : base;
        }
        function renderTimeline() {
            const mems = data.memories.filter(visibleMemory).sort((a, b) => new Date(b.created) - new Date(a.created));
            const days = new Map();
            for (const m of mems) {
                const k = new Date(m.created).toDateString();
                if (!days.has(k)) days.set(k, { date: m.created, groups: new Map() });
                const gk = m.sources.length ? m.sources[0].conversationId : 'manual';
                const day = days.get(k);
                if (!day.groups.has(gk)) day.groups.set(gk, []);
                day.groups.get(gk).push(m);
            }
            let html = '';
            for (const day of days.values()) {
                html += `<div class="day">${esc(dayLabel(day.date))}</div>`;
                for (const [gk, list] of day.groups) {
                    const s = gk === 'manual' ? null : sessionOf(gk);
                    const head = gk === 'manual'
                        ? `<div class="ct">${esc(T.manual)}</div>`
                        : s
                            ? `<div class="ct"><button class="link" data-open="${esc(gk)}" data-seq="${list[0].sources[0].seq}">${esc(s.title)}</button><small>${esc(fmtTime(list[0].created))}</small></div>`
                            : `<div class="ct warn">${esc(T.sourceGone)}<small>${esc(fmtTime(list[0].created))}</small></div>`;
                    html += `<div class="conv${gk === 'manual' ? ' manual' : ''}">${head}${list.map(m => memHtml(m, false)).join('')}</div>`;
                }
            }
            $('timeline').innerHTML = html || `<div id="empty" class="show">${esc(data.memories.length === 0 ? T.empty : T.emptyFiltered)}</div>`;
            wireRows($('timeline'));
            if (highlight.size) { const first = $('timeline').querySelector('.mem.hl'); if (first) first.scrollIntoView({ block: 'center' }); }
        }

        // graph -----------------------------------------------------------
        function mkNode(kind, d) {
            const p = pos.get(d.id);
            const a = (hash(d.id) % 360) * Math.PI / 180;
            return { id: d.id, kind, d, x: p ? p.x : Math.cos(a) * 160 + (rnd(hash(d.id)) - .5) * 40, y: p ? p.y : Math.sin(a) * 130 + (rnd(hash(d.id) + 1) - .5) * 40, vx: 0, vy: 0, fresh: !p, r: kind === 'm' ? 7 + Math.min(10, Math.sqrt(d.uses || 0) * 2.2) : 10, el: null };
        }
        function rebuild(fit) {
            // Everything the filters allow, and what joins it.
            const memIds = new Set(data.memories.filter(visibleMemory).map(m => m.id));
            const all = new Map();
            for (const m of data.memories) if (memIds.has(m.id)) all.set(m.id, ['m', m]);
            const need = new Set();
            for (const e of data.edges) {
                if (memIds.has(e.a) && !memIds.has(e.b)) need.add(e.b);
                if (memIds.has(e.b) && !memIds.has(e.a)) need.add(e.a);
                if (e.kind === 'similar') { need.add(e.a); need.add(e.b); }
            }
            for (const s of data.sessions) if (need.has(s.id) || s.current) all.set(s.id, ['s', s]);
            const edges = data.edges.filter(e => all.has(e.a) && all.has(e.b));
            fullAdj = new Map([...all.keys()].map(k => [k, new Set()]));
            for (const e of edges) { fullAdj.get(e.a).add(e.b); fullAdj.get(e.b).add(e.a); }
            if (selected && !all.has(selected.id)) selected = null;

            // With something selected, by default only it and what it connects to.
            let keep = new Set(all.keys());
            if (selected && focusOnly) keep = new Set([selected.id, ...(fullAdj.get(selected.id) || [])]);
            nodes = []; links = [];
            for (const id of keep) { const [kind, d] = all.get(id); nodes.push(mkNode(kind, d)); }
            byId = new Map(nodes.map(n => [n.id, n]));
            for (const e of edges) { const a = byId.get(e.a), b = byId.get(e.b); if (a && b) links.push({ a, b, kind: e.kind }); }
            adj = new Map(nodes.map(n => [n.id, new Set()]));
            for (const l of links) { adj.get(l.a.id).add(l.b.id); adj.get(l.b.id).add(l.a.id); }

            const note = $('graphNote');
            if (!selected && data.memories.length && nodes.length < 4) { note.textContent = T.fewNodes; note.classList.add('show'); }
            else if (!selected && nodes.length >= 4) { note.textContent = T.pickOne; note.classList.add('show'); }
            else note.classList.remove('show');
            $('focusbar').style.display = selected ? '' : 'none';
            $('focusbar').classList.toggle('off', !focusOnly);
            $('focusbar').textContent = (focusOnly ? '☑ ' : '☐ ') + T.focusOnly;
            draw();
            layout();
            if (fit) fitView();
        }
        function draw() {
            const ge = $('edges'), gn = $('nodes'); ge.replaceChildren(); gn.replaceChildren();
            for (const l of links) { l.el = document.createElementNS(SVGNS, 'line'); l.el.setAttribute('class', 'edge ' + l.kind); ge.append(l.el); }
            for (const n of nodes) {
                const g = document.createElementNS(SVGNS, 'g');
                g.setAttribute('class', 'node ' + (n.kind === 'm' ? n.d.status : 'session') + (n.d.current ? ' current' : ''));
                const shape = n.kind === 'm' ? document.createElementNS(SVGNS, 'circle') : document.createElementNS(SVGNS, 'rect');
                if (n.kind === 'm') { shape.setAttribute('r', n.r); shape.setAttribute('class', 'shape t-' + n.d.type); }
                else { shape.setAttribute('x', -n.r); shape.setAttribute('y', -n.r); shape.setAttribute('width', n.r * 2); shape.setAttribute('height', n.r * 2); shape.setAttribute('rx', 3); shape.setAttribute('class', 'shape s'); }
                const ring = n.kind === 'm' ? document.createElementNS(SVGNS, 'circle') : document.createElementNS(SVGNS, 'rect');
                if (n.kind === 'm') ring.setAttribute('r', n.r + 4); else { ring.setAttribute('x', -n.r - 4); ring.setAttribute('y', -n.r - 4); ring.setAttribute('width', n.r * 2 + 8); ring.setAttribute('height', n.r * 2 + 8); ring.setAttribute('rx', 6); }
                ring.setAttribute('class', 'ring');
                const text = document.createElementNS(SVGNS, 'text'); text.setAttribute('y', n.kind === 'm' ? n.r + 13 : -(n.r + 8)); text.setAttribute('text-anchor', 'middle');
                text.textContent = cut(n.kind === 'm' ? n.d.text : n.d.title, 14);
                g.append(shape, ring, text); gn.append(g); n.el = g; n.label = text;
                g.addEventListener('pointerdown', ev => startDrag(ev, n));
                g.addEventListener('pointerenter', () => { hover = n; paint(); showTip(n); });
                g.addEventListener('pointerleave', () => { hover = null; paint(); hideTip(); });
            }
            paint();
        }
        function layout() {
            // The same graph looks the same each time: nodes keep where they were, and new ones start at a fixed spot.
            const freshAll = nodes.every(n => n.fresh);
            const steps = freshAll ? 280 : 90;
            for (let i = 0; i < steps; i++) step((freshAll ? 1 : 0.35) * (1 - i / (steps + 40)));
            for (const n of nodes) { pos.set(n.id, { x: n.x, y: n.y }); n.fresh = false; }
            place();
        }
        function step(alpha) {
            const n = nodes.length;
            for (let i = 0; i < n; i++) {
                const a = nodes[i];
                for (let j = i + 1; j < n; j++) {
                    const b = nodes[j]; let dx = a.x - b.x, dy = a.y - b.y, d2 = dx * dx + dy * dy;
                    if (d2 > 90000) continue;
                    if (d2 < 1) { dx = rnd(i * 31 + j) - .5; dy = rnd(i * 17 + j) - .5; d2 = 1; }
                    const f = 2600 / d2 * alpha, d = Math.sqrt(d2);
                    a.vx += dx / d * f; a.vy += dy / d * f; b.vx -= dx / d * f; b.vy -= dy / d * f;
                }
            }
            for (const l of links) {
                const dx = l.b.x - l.a.x, dy = l.b.y - l.a.y, d = Math.sqrt(dx * dx + dy * dy) || 1;
                const want = l.kind === 'source' ? 150 : 150, f = (d - want) * 0.02;
                l.a.vx += dx / d * f; l.a.vy += dy / d * f; l.b.vx -= dx / d * f; l.b.vy -= dy / d * f;
            }
            for (const a of nodes) { a.vx -= a.x * 0.004; a.vy -= a.y * 0.004; a.x += a.vx; a.y += a.vy; a.vx *= 0.82; a.vy *= 0.82; }
        }
        function place() {
            for (const n of nodes) if (n.el) n.el.setAttribute('transform', `translate(${n.x.toFixed(1)},${n.y.toFixed(1)})`);
            for (const l of links) if (l.el) { l.el.setAttribute('x1', l.a.x); l.el.setAttribute('y1', l.a.y); l.el.setAttribute('x2', l.b.x); l.el.setAttribute('y2', l.b.y); }
        }
        function fitView() {
            if (!nodes.length) return;
            const xs = nodes.map(n => n.x), ys = nodes.map(n => n.y);
            const x0 = Math.min(...xs) - 70, x1 = Math.max(...xs) + 70, y0 = Math.min(...ys) - 70, y1 = Math.max(...ys) + 70;
            const w = $('svg').clientWidth, h = $('svg').clientHeight;
            if (!w || !h) return;
            vp.k = Math.min(1.4, Math.min(w / (x1 - x0), h / (y1 - y0)));
            vp.x = w / 2 - (x0 + x1) / 2 * vp.k; vp.y = h / 2 - (y0 + y1) / 2 * vp.k; applyVp();
        }
        function applyVp() {
            $('vp').setAttribute('transform', `translate(${vp.x},${vp.y}) scale(${vp.k})`);
            const near = selected ? (fullAdj.get(selected.id) || new Set()) : new Set();
            for (const n of nodes) if (n.label) n.label.style.display = (vp.k >= 0.75 || n === hover || (selected && (selected.id === n.id || near.has(n.id)))) ? '' : 'none';
        }
        function paint() {
            const focus = selected ? new Set([selected.id, ...(adj.get(selected.id) || [])]) : hover ? new Set([hover.id, ...(adj.get(hover.id) || [])]) : null;
            const hitIds = hits ? new Set([...hits.memories, ...hits.sessions.map(s => s.id)]) : null;
            for (const n of nodes) {
                n.el.classList.toggle('sel', !!selected && selected.id === n.id);
                n.el.classList.toggle('hit', !!hitIds && hitIds.has(n.id));
                n.el.classList.toggle('dim', (focus && !focus.has(n.id)) || (hitIds && !hitIds.has(n.id)));
            }
            for (const l of links) l.el.classList.toggle('dim', !!(focus && !(focus.has(l.a.id) && focus.has(l.b.id))) || !!hitIds);
            applyVp();
        }
        $('focusbar').onclick = () => { focusOnly = !focusOnly; rebuild(true); };
        function showTip(n) {
            const tip = $('tip');
            tip.innerHTML = n.kind === 'm'
                ? `${esc(cut(n.d.text, 220))}<small>${esc(T[n.d.type])} · ${esc(T[n.d.status])}</small>`
                : `${esc(cut(n.d.title, 120))}<small>${esc(fmtDay(n.d.at))}</small>`;
            tip.style.left = (vp.x + n.x * vp.k) + 'px';
            tip.style.top = (vp.y + n.y * vp.k - (n.r + 8) * vp.k) + 'px';
            tip.classList.add('show');
        }
        function hideTip() { $('tip').classList.remove('show'); }

        const svg = $('svg');
        svg.addEventListener('pointerdown', ev => {
            if (ev.target !== svg && ev.target.id !== 'vp') return;
            const sx = ev.clientX, sy = ev.clientY, ox = vp.x, oy = vp.y; let moved = false;
            svg.classList.add('panning'); svg.setPointerCapture(ev.pointerId);
            const move = e => { if (Math.abs(e.clientX - sx) + Math.abs(e.clientY - sy) > 3) { moved = true; userMoved = true; hideTip(); } vp.x = ox + e.clientX - sx; vp.y = oy + e.clientY - sy; applyVp(); };
            const up = () => { svg.classList.remove('panning'); svg.removeEventListener('pointermove', move); svg.removeEventListener('pointerup', up); if (!moved) { selected = null; hits = null; $('results').classList.remove('show'); renderAll(true); } };
            svg.addEventListener('pointermove', move); svg.addEventListener('pointerup', up);
        });
        svg.addEventListener('wheel', ev => {
            ev.preventDefault();
            const f = Math.exp(-ev.deltaY * 0.0015), k = Math.max(0.15, Math.min(3.5, vp.k * f)), r = svg.getBoundingClientRect(), px = ev.clientX - r.left, py = ev.clientY - r.top;
            vp.x = px - (px - vp.x) * (k / vp.k); vp.y = py - (py - vp.y) * (k / vp.k); vp.k = k; userMoved = true; applyVp(); hideTip();
        }, { passive: false });
        function startDrag(ev, n) {
            ev.stopPropagation();
            const sx = ev.clientX, sy = ev.clientY, ox = n.x, oy = n.y; let moved = false;
            svg.setPointerCapture(ev.pointerId);
            const move = e => { if (Math.abs(e.clientX - sx) + Math.abs(e.clientY - sy) > 3) { moved = true; hideTip(); } n.x = ox + (e.clientX - sx) / vp.k; n.y = oy + (e.clientY - sy) / vp.k; pos.set(n.id, { x: n.x, y: n.y }); place(); };
            const up = () => { svg.removeEventListener('pointermove', move); svg.removeEventListener('pointerup', up); if (!moved) select(n); };
            svg.addEventListener('pointermove', move); svg.addEventListener('pointerup', up);
        }
        function select(n) { editing = false; selected = { kind: n.kind, id: n.id }; hits = null; $('results').classList.remove('show'); renderAll(true); }
        window.addEventListener('resize', () => applyVp());

        // ---------- side panel
        function renderSide() {
            const side = $('side');
            if (editing) return;
            if (!selected) { side.classList.remove('show'); side.replaceChildren(); return; }
            side.classList.add('show');
            if (selected.kind === 'm') memoryPanel(side, data.memories.find(m => m.id === selected.id)); else sessionPanel(side, data.sessions.find(s => s.id === selected.id));
        }
        function memoryPanel(side, m) {
            if (!m) { side.classList.remove('show'); return; }
            const origin = { user: T.originUser, assistant: T.originAssistant, consolidation: T.originConsolidation }[m.origin] || m.origin;
            side.innerHTML =
                `<h2>${esc(T[m.type])}</h2>
                 <div><label>${esc(T.text)}</label><textarea id="f-text">${esc(m.text)}</textarea></div>
                 <div><label>${esc(T.type)}</label><select id="f-type">${['preference', 'project', 'decision', 'fact'].map(t => `<option value="${t}" ${t === m.type ? 'selected' : ''}>${esc(T[t])} — ${esc(T[t + 'Help'])}</option>`).join('')}</select></div>
                 <div><label>${esc(T.status)}</label><select id="f-status"><option value="new" ${m.status === 'new' ? 'selected' : ''}>${esc(T.new)} — ${esc(T.newHelp)}</option><option value="settled" ${m.status !== 'new' ? 'selected' : ''}>${esc(T.settled)} — ${esc(T.settledHelp)}</option></select></div>
                 ${m.status === 'archived' ? `<div class="warn">${esc(T.archivedNote)} <button class="link" id="f-revive">${esc(T.revive)}</button></div>` : ''}
                 <div class="meta">${esc(T.scope)}: ${esc(T[m.scope])}<br>${usage(m)}<br>${esc(T.created)}: ${esc(fmt(m.created))}<br>${esc(T.updated)}: ${esc(fmt(m.updated))}<br>${esc(T.origin)}: ${esc(origin)}</div>
                 ${m.sources.length ? `<div><label>${esc(T.sources)}</label><div class="list">${m.sources.map(s => { const c = sessionOf(s.conversationId); return c ? `<div class="item" data-open="${esc(s.conversationId)}" data-seq="${s.seq}">${esc(c.title)}<small>${esc(fmtDay(c.at))}</small></div>` : `<div class="warn">${esc(T.sourceGone)}</div>`; }).join('')}</div></div>` : ''}
                 ${m.related.length ? `<div><label>${esc(T.related)}</label><div class="list">${m.related.map(id => { const r = data.memories.find(x => x.id === id); return r ? `<div class="item" data-sel="${esc(id)}">${esc(cut(r.text, 70))}</div>` : ''; }).join('')}</div></div>` : ''}
                 <div class="row"><button class="primary" id="f-save">${esc(T.save)}</button><button class="danger" id="f-forget">${esc(T.forget)}</button></div>`;
            $('f-save').onclick = () => post({ type: 'save', id: m.id, text: $('f-text').value, memType: $('f-type').value, status: $('f-status').value });
            $('f-forget').onclick = () => { selected = null; post({ type: 'forget', id: m.id }); };
            if ($('f-revive')) $('f-revive').onclick = () => post({ type: 'revive', id: m.id });
            wireItems(side);
        }
        function sessionPanel(side, s) {
            if (!s) { side.classList.remove('show'); return; }
            const mem = data.memories.filter(m => m.sources.some(x => x.conversationId === s.id));
            side.innerHTML =
                `<h2>${esc(s.title)}</h2>
                 <div class="meta">${esc(fmt(s.at))}${s.current ? '<br>' + esc(T.currentConversation) : ''}</div>
                 <div class="row"><button class="primary" id="f-open" data-open="${esc(s.id)}" data-seq="0">${esc(T.open)}</button></div>
                 ${mem.length ? `<div><label>${esc(T.notesFrom)}</label><div class="list">${mem.map(m => `<div class="item" data-sel="${esc(m.id)}">${esc(cut(m.text, 70))}</div>`).join('')}</div></div>` : ''}`;
            wireItems(side);
        }
        function wireItems(root) {
            root.querySelectorAll('[data-open]').forEach(el => el.onclick = () => post({ type: 'openConversation', id: el.dataset.open, seq: +el.dataset.seq || 0 }));
            root.querySelectorAll('[data-sel]').forEach(el => el.onclick = () => { const m = data.memories.find(x => x.id === el.dataset.sel); if (m) { editing = false; selected = { kind: 'm', id: m.id }; highlight.clear(); renderAll(true); } });
        }

        // ---------- search
        let searchTimer = 0;
        $('q').addEventListener('input', () => { clearHighlight(true); clearTimeout(searchTimer); searchTimer = setTimeout(() => { const q = $('q').value.trim(); if (!q) { hits = null; $('results').classList.remove('show'); if (view === 'graph') paint(); return; } post({ type: 'search', query: q }); }, 280); });
        function showResults(m) {
            hits = { memories: m.memories.map(x => x.id), sessions: m.sessions };
            const r = $('results'); r.classList.add('show');
            const none = !m.memories.length && !m.sessions.length;
            r.innerHTML =
                (none ? `<div class="warn">${esc(T.nothingFound)}</div>` : '') +
                (m.memories.length ? `<h3>${esc(T.inMemories)}</h3>` + m.memories.map(x => `<div class="item" data-sel="${esc(x.id)}">${esc(cut(x.text, 90))}</div>`).join('') : '') +
                (m.sessions.length ? `<h3>${esc(T.inConversations)}</h3>` + m.sessions.map(s => `<div class="item" data-open="${esc(s.id)}" data-seq="${s.seq}">${esc(s.title)}<small>${esc(fmtDay(s.at))} · ${esc(cut(s.snippet, 110))}</small></div>`).join('') : '');
            wireItems(r);
            if (view === 'graph') paint();
        }

        // ---------- toast
        let toastTimer = 0;
        function toast(text, undoId) {
            $('toast-text').textContent = text; $('toast-undo').style.display = undoId ? '' : 'none'; lastForget = undoId || null;
            $('toast').classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').classList.remove('show'), 6000);
        }
        $('toast-undo').onclick = () => { if (lastForget) post({ type: 'restore', id: lastForget }); $('toast').classList.remove('show'); };

        // ---------- show the memories an answer was given (from the reply footer)
        function focusOn(ids) {
            highlight = new Set(ids); selected = null; hits = null; editing = false;
            clearTimeout(hlTimer); hlTimer = setTimeout(() => clearHighlight(true), 12000);
            if (view !== 'list') { const keep = highlight; setView('list'); highlight = keep; renderAll(false); toast(T.highlighted); return; }
            renderAll(false);
            toast(T.highlighted);
        }

        // ---------- add a memory
        function openAdd() {
            clearHighlight(false); selected = null; editing = false; renderAll(false); editing = true;
            const side = $('side'); side.classList.add('show');
            side.innerHTML =
                `<h2>${esc(T.newNote)}</h2>
                 <div class="help">${esc(T.newNoteHelp)}</div>
                 <div><label>${esc(T.text)}</label><textarea id="n-text"></textarea></div>
                 <div><label>${esc(T.type)}</label><select id="n-type">${['preference', 'project', 'decision', 'fact'].map(t => `<option value="${t}">${esc(T[t])} — ${esc(T[t + 'Help'])}</option>`).join('')}</select></div>
                 <div><label>${esc(T.scope)}</label><select id="n-scope"><option value="global">${esc(T.global)}</option><option value="sandbox">${esc(T.sandbox)}</option></select></div>
                 <div class="row"><button class="primary" id="n-create">${esc(T.create)}</button><button id="n-cancel">${esc(T.cancel)}</button></div>`;
            $('n-text').focus();
            const done = () => { editing = false; side.classList.remove('show'); side.replaceChildren(); };
            $('n-create').onclick = () => { post({ type: 'create', text: $('n-text').value, memType: $('n-type').value, scope: $('n-scope').value }); done(); };
            $('n-cancel').onclick = done;
        }
        $('b-add').onclick = openAdd;

        // ---------- tidying up
        const dlg = $('dialog'), overlay = $('overlay');
        function closeOverlay() { overlay.classList.remove('show'); dlg.replaceChildren(); }
        function openDialog(html) { dlg.innerHTML = html; overlay.classList.add('show'); }
        function startConsolidate() { if (!enabled) return; openDialog(`<div class="warn">…</div>`); post({ type: 'estimate' }); }
        $('b-consolidate').onclick = startConsolidate; $('banner-go').onclick = startConsolidate;
        function showEstimate(m) {
            const cost = m.cost ? ` (${m.cost})` : '';
            openDialog(`<h2>${esc(T.consolidate)}</h2><p>${esc(T.estimate.replace('{0}', m.notes).replace('{1}', m.conversations).replace('{2}', m.tokens).replace('{3}', cost))}</p>
                <div class="row"><button class="primary" id="d-run">${esc(T.run)}</button><button id="d-cancel">${esc(T.cancel)}</button></div>`);
            $('d-cancel').onclick = closeOverlay;
            $('d-run').onclick = () => { openDialog(`<h2>${esc(T.consolidate)}</h2><p class="warn">${esc(T.asking)}</p><div class="row"><button id="d-cancel2">${esc(T.cancel)}</button></div>`); $('d-cancel2').onclick = () => { post({ type: 'cancelConsolidate' }); closeOverlay(); }; post({ type: 'consolidate' }); };
        }
        function showPlan(m) {
            const name = { add: T.opAdd, merge: T.opMerge, update: T.opUpdate, retire: T.opRetire, link: T.opLink, settle: T.opSettle };
            const note = id => { const x = data.memories.find(y => y.id === id); return x ? x.text : id; };
            let html = `<h2>${esc(T.planTitle)}</h2>`;
            if (m.dropped) html += `<div class="warn">${esc(T.dropped.replace('{0}', m.dropped))}</div>`;
            if (!m.ops.length) html += `<p>${esc(T.noSuggestions)}</p><div class="row"><button id="d-close">${esc(T.close)}</button></div>`;
            else {
                html += m.ops.map((o, i) => {
                    const body = o.kind === 'add' ? `<b>${esc(o.text)}</b>`
                        : o.kind === 'update' ? `<span class="old">${esc(note(o.ids[0]))}</span><b>${esc(o.text)}</b>`
                        : o.kind === 'merge' ? o.ids.map(id => `<span class="old">${esc(note(id))}</span>`).join('') + `<b>${esc(o.text)}</b>`
                        : o.kind === 'link' ? o.ids.map(id => `<span>${esc(note(id))}</span>`).join('<span class="warn"> ↔ </span>')
                        : `<span>${esc(note(o.ids[0]))}</span>${o.kind === 'retire' ? ' <span class="warn">' + esc(T.opRetireNote) + '</span>' : ''}`;
                    return `<label class="op"><input type="checkbox" data-op="${i}" checked><span><span class="tag">${esc(name[o.kind])}</span>${body}<span class="reason">${esc(o.reason)}</span></span></label>`;
                }).join('');
                html += `<div class="row"><button class="primary" id="d-apply">${esc(T.apply)}</button><button id="d-close">${esc(T.cancel)}</button></div>`;
            }
            openDialog(html);
            $('d-close').onclick = closeOverlay;
            if ($('d-apply')) $('d-apply').onclick = () => post({ type: 'apply', ops: [...dlg.querySelectorAll('input[data-op]')].filter(c => c.checked).map(c => +c.dataset.op) });
        }
        function showError(text) { openDialog(`<h2>${esc(T.title)}</h2><p>${esc(text)}</p><div class="row"><button id="d-close">${esc(T.close)}</button></div>`); $('d-close').onclick = closeOverlay; }
        overlay.addEventListener('pointerdown', e => { if (e.target === overlay) closeOverlay(); });

        post({ type: 'ready' });
        </script>
        </body>
        </html>
        """;
}
