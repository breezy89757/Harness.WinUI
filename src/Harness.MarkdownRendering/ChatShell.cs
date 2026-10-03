// Harness.WinUI — Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Harness.MarkdownRendering;

/// <summary>
/// Builds the WebView2 shell HTML for the chat page: one page loaded once at startup,
/// with per-message DOM append/update afterwards (never a full-page replace).
///
/// Host app must call <c>CoreWebView2.SetVirtualHostNameToFolderMapping(VirtualHostName, "wwwroot", ...)</c>
/// pointing at this assembly's wwwroot output folder.
///
/// Each turn is <c>div.turn.{role}#msg-{id}</c> containing a bubble with <c>.steps</c> (tool calls /
/// reasoning), <c>.content</c> (rendered Markdown) and <c>.status</c> (animated "working" line with a
/// live elapsed-time counter), followed by a muted <c>.meta</c> line (model, tokens, timing).
///
/// Streaming render decisions:
///  - Text deltas are buffered by the caller and flushed to <c>updateMessageContent</c> on a
///    throttle (not per-token) — re-parsing Markdown on every token is wasted work and flickers.
///  - Syntax highlighting re-runs scoped to the single message on every flush; Mermaid only renders
///    once <c>isFinal</c> is true, because a half-streamed diagram is invalid Mermaid syntax.
///  - Auto-scroll only fires when the user is already near the bottom, so scrolling up to re-read
///    earlier output isn't fought by new tokens.
///  - The endpoint tested so far sends a reply as one burst after ~3s of silence, so the status
///    line (not token-by-token text) is what tells the user the agent is alive.
/// </summary>
public static class ChatShell
{
    public const string VirtualHostName = "harness.assets";

    /// <summary>Host the app maps to the generated-images folder, so tool results can show them.</summary>
    public const string ImagesHostName = "harness.images";

    /// <summary>
    /// Parent domain for MCP App views: each view gets its own origin (<c>https://{viewId}.harness.apps</c>),
    /// so views can't reach each other or the chat page.
    /// </summary>
    public const string AppsHostSuffix = "harness.apps";

    public static string GetShellHtml(int fontSize = 14)
    {
        // Only our own nonce'd inline scripts and bundled assets may run: blocks inline event
        // handlers and javascript: URLs, and remote images (a common exfiltration channel for
        // prompt-injected output). ExecuteScriptAsync from the host isn't subject to CSP.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var assets = "https://" + VirtualHostName;

        var sb = new StringBuilder(8192);
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
        sb.Append($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; " +
                  $"script-src 'nonce-{nonce}' {assets}; style-src 'unsafe-inline' {assets}; " +
                  $"img-src data: {assets} https://{ImagesHostName}; font-src data: {assets}; frame-src https://*.{AppsHostSuffix}; " +
                  "base-uri 'none'; form-action 'none'\">");
        sb.Append(GetStyles(fontSize));
        sb.Append("</head><body>");
        sb.Append("<div id='messages'></div>");
        sb.Append(GetHighlightAndMermaidLinks(nonce));
        sb.Append(GetChatScript(nonce));
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GetStyles(int fontSize) => $$"""
        <style>
        :root {
            --text-color: #24292f; --muted: #6e7781; --bg-color: #ffffff; --code-bg: #f6f8fa;
            --border-color: #d0d7de; --bubble-user-bg: #eef4ff; --bubble-assistant-bg: #f6f8fa;
            --ok: #1a7f37; --err: #cf222e; --warn: #9a6700; --accent: #0969da;
        }
        @media (prefers-color-scheme: dark) {
            :root {
                --text-color: #e6edf3; --muted: #8b949e; --bg-color: #0d1117; --code-bg: #161b22;
                --border-color: #30363d; --bubble-user-bg: #17304d; --bubble-assistant-bg: #161b22;
                --ok: #3fb950; --err: #f85149; --warn: #d29922; --accent: #58a6ff;
            }
        }
        * { box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
            font-size: {{fontSize}}px; line-height: 1.6; color: var(--text-color);
            background-color: var(--bg-color); margin: 0; padding: 16px 24px 96px;
        }
        #messages { display: flex; flex-direction: column; gap: 14px; max-width: 900px; margin: 0 auto; }
        .turn { display: flex; flex-direction: column; max-width: 88%; }
        .turn.user { align-self: flex-end; align-items: flex-end; }
        .turn.assistant { align-self: flex-start; align-items: flex-start; }
        .turn.system { align-self: center; align-items: center; opacity: 0.75; font-size: 0.85em; }
        .message { padding: 10px 16px; border-radius: 12px; word-wrap: break-word; max-width: 100%; }
        .turn.user .message { background: var(--bubble-user-bg); }
        .turn.assistant .message { background: var(--bubble-assistant-bg); }
        /* Bubbles shrink to fit their text, but diagrams/code/tables have no intrinsic width of their own —
           a Mermaid SVG at width:100% of a narrow bubble renders tiny. Give those replies the full column. */
        .turn.assistant:has(.mermaid, [data-mermaid-hash], pre, table, .artifact-card, .mcp-app) { align-self: stretch; }
        .turn.assistant:has(.mermaid, [data-mermaid-hash], pre, table, .artifact-card, .mcp-app) .message { width: 100%; }
        .attachments { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; justify-content: flex-end; align-items: flex-start; }
        .attachment-image { max-width: 240px; max-height: 180px; border-radius: 8px; border: 1px solid var(--border-color); }
        .attachment-file { font-size: 0.85em; padding: 3px 10px; border-radius: 999px; border: 1px solid var(--border-color);
                           background: var(--bg-color); white-space: nowrap; max-width: 260px; overflow: hidden; text-overflow: ellipsis; }
        /* Shown while files are dragged over the conversation. */
        #drop-hint { position: fixed; inset: 8px; border: 2px dashed var(--accent); border-radius: 12px; display: none;
                     align-items: center; justify-content: center; font-size: 1.1em; color: var(--accent);
                     background: color-mix(in srgb, var(--bg-color) 85%, transparent); z-index: 100; pointer-events: none; }
        body.dragging #drop-hint { display: flex; }
        .apps { display: flex; flex-direction: column; gap: 8px; margin-bottom: 8px; }
        /* content-box: the height the view reports is its content; a border must not eat into it. */
        .mcp-app { display: block; box-sizing: content-box; width: 100%; height: 160px; border: 0; border-radius: 10px; background: transparent; }
        .mcp-app.bordered { width: calc(100% - 2px); }
        .mcp-app.bordered { border: 1px solid var(--border-color); }
        .mermaid svg { max-width: 100%; height: auto; }
        .content:empty { display: none; }

        .artifact-card { display: flex; align-items: center; gap: 12px; margin: 8px 0; padding: 10px 14px;
                         border: 1px solid var(--border-color); border-radius: 10px; background: var(--bg-color);
                         cursor: pointer; max-width: 420px; }
        .artifact-card:hover { border-color: var(--accent); }
        .artifact-card:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
        .artifact-badge { flex: none; min-width: 44px; height: 36px; padding: 0 6px; border-radius: 8px;
                          display: grid; place-items: center; font: 600 12px 'Cascadia Code', Consolas, monospace;
                          color: var(--accent); background: var(--code-bg); border: 1px solid var(--border-color); }
        .artifact-meta { min-width: 0; }
        .artifact-title { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .artifact-sub { font-size: 0.85em; color: var(--muted); }
        .artifact-card.writing .artifact-badge { animation: harness-pulse 1.4s ease-in-out infinite; }
        @keyframes harness-pulse { 50% { opacity: 0.45; } }
        .content > :first-child { margin-top: 0; }
        .content > :last-child { margin-bottom: 0; }

        .status { display: flex; align-items: center; gap: 8px; color: var(--muted); font-size: 0.9em; }
        .status[hidden] { display: none; }
        .content:not(:empty) + .status { margin-top: 8px; }
        .status .elapsed { font-variant-numeric: tabular-nums; opacity: 0.8; }
        .dots { display: inline-flex; gap: 4px; }
        .dots i { width: 6px; height: 6px; border-radius: 50%; background: currentColor; opacity: 0.3;
                  animation: harness-blink 1.2s infinite ease-in-out; }
        .dots i:nth-child(2) { animation-delay: 0.2s; }
        .dots i:nth-child(3) { animation-delay: 0.4s; }
        @keyframes harness-blink { 0%, 80%, 100% { opacity: 0.25; transform: translateY(0); }
                                40% { opacity: 1; transform: translateY(-2px); } }

        .steps { display: flex; flex-direction: column; gap: 4px; font-size: 0.85em; color: var(--muted);
                 border-left: 2px solid var(--border-color); padding-left: 10px; margin-bottom: 8px; }
        .steps:empty { display: none; }
        .step { display: flex; align-items: baseline; gap: 6px; }
        .step::before { flex: none; width: 1em; text-align: center; }
        .step.running::before { content: ''; width: 9px; height: 9px; border-radius: 50%;
                                border: 2px solid var(--accent); border-right-color: transparent;
                                animation: harness-spin 0.8s linear infinite; align-self: center; }
        .step.done::before { content: '\2713'; color: var(--ok); }
        .step.error::before { content: '\2715'; color: var(--err); }
        .step.waiting::before { content: '?'; color: var(--warn); font-weight: 700; }
        .step.waiting { color: var(--text-color); }
        .step > div { min-width: 0; flex: 1; }
        .tool-server { opacity: 0.7; margin-left: 4px; }
        .tool-args, .tool-result { margin: 6px 0 0; padding: 8px; font-size: 0.9em; max-height: 220px; overflow: auto;
                                   white-space: pre-wrap; word-break: break-word; }
        .tool-open { margin-left: 6px; white-space: nowrap; }
        .tool-image { display: block; margin-top: 8px; max-width: min(100%, 420px); max-height: 420px;
                      border-radius: 10px; border: 1px solid var(--border-color); cursor: zoom-in; }
        .approval { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 8px; }
        .approval span { margin-right: 4px; }
        .approval button { font: inherit; padding: 4px 12px; border-radius: 6px; cursor: pointer;
                           border: 1px solid var(--border-color); background: var(--bg-color); color: var(--text-color); }
        .approval button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
        .approval button:disabled { opacity: 0.5; cursor: default; }
        .approval button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
        .step code { font-size: 0.95em; }
        .step details { width: 100%; }
        .step summary { cursor: pointer; }
        .step .reasoning-text { white-space: pre-wrap; margin-top: 4px; }
        @keyframes harness-spin { to { transform: rotate(360deg); } }

        /* Markdown tables: a light frame, a divider under the header, row lines and alternate shading. */
        .message table { border-collapse: separate; border-spacing: 0; margin: 8px 0; max-width: 100%; width: fit-content; display: block;
                         overflow-x: auto; border: 1px solid var(--border-color); border-radius: 8px; }
        .message th, .message td { padding: 6px 14px; text-align: left; border-bottom: 1px solid var(--border-color);
                                   font-variant-numeric: tabular-nums; }
        .message th { font-weight: 600; background: var(--code-bg); border-bottom-width: 2px; }
        .message tbody tr:nth-child(even) td { background: color-mix(in srgb, var(--code-bg) 60%, transparent); }
        .message tbody tr:last-child td { border-bottom: none; }
        .meta { font-size: 0.75em; color: var(--muted); margin-top: 4px; padding: 0 6px; cursor: default; }
        .meta:empty { display: none; }
        .meta.traced { cursor: pointer; }
        .meta.traced:hover { color: var(--accent); text-decoration: underline; }

        @media (prefers-reduced-motion: reduce) {
            .dots i, .step.running::before, .artifact-card.writing .artifact-badge { animation: none; opacity: 0.7; }
        }

        pre { background-color: var(--code-bg); padding: 12px; border-radius: 8px; overflow-x: auto; border: 1px solid var(--border-color); }
        code { font-family: 'Cascadia Code', 'Cascadia Mono', Consolas, monospace; font-size: 0.9em; }
        pre code { background: none; padding: 0; }
        </style>
        """;

    private static string GetHighlightAndMermaidLinks(string nonce) => """
        <link rel="stylesheet" href="https://__HOST__/css/github.min.css" media="(prefers-color-scheme: light)">
        <link rel="stylesheet" href="https://__HOST__/css/github-dark.min.css" media="(prefers-color-scheme: dark)">
        <script src="https://__HOST__/js/highlight.min.js"></script>
        <script src="https://__HOST__/js/mermaid.min.js"></script>
        <script nonce="__NONCE__">
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });
        </script>
        """.Replace("__HOST__", VirtualHostName).Replace("__NONCE__", nonce);

    private static string GetChatScript(string nonce) => """
        <script nonce="__NONCE__">
        const AutoScrollThresholdPx = 48;
        const statusTimers = new Map();

        function isNearBottom() {
            const s = document.scrollingElement || document.documentElement;
            return s.scrollHeight - s.scrollTop - s.clientHeight < AutoScrollThresholdPx;
        }

        function scrollToBottom() {
            const s = document.scrollingElement || document.documentElement;
            s.scrollTop = s.scrollHeight;
        }

        function turn(id) { return document.getElementById('msg-' + id); }

        function clearMessages() {
            statusTimers.forEach(t => { if (t.handle) clearInterval(t.handle); });
            statusTimers.clear();
            appViews.clear();
            document.getElementById('messages').replaceChildren();
        }

        // MCP App views: sandboxed iframes, each on its own origin, served by the host. This page only
        // relays JSON-RPC between a view and the host, and only for the view's own frame and origin.
        const appViews = new Map();

        function appendAppView(id, viewId, origin, prefersBorder) {
            const t = turn(id);
            if (!t) return;
            withStickyScroll(() => {
                let box = t.querySelector('.apps');
                if (!box) {
                    box = document.createElement('div');
                    box.className = 'apps';
                    t.querySelector('.content').before(box);
                }
                const frame = document.createElement('iframe');
                frame.className = 'mcp-app' + (prefersBorder ? ' bordered' : '');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.setAttribute('referrerpolicy', 'no-referrer');
                frame.title = 'MCP App';
                frame.src = origin + '/';
                box.appendChild(frame);
                appViews.set(viewId, { frame, origin });
            });
        }

        function deliverToApp(viewId, message) {
            const view = appViews.get(viewId);
            if (view && view.frame.contentWindow) view.frame.contentWindow.postMessage(message, view.origin);
        }

        function setAppHeight(viewId, height) {
            const view = appViews.get(viewId);
            if (view) withStickyScroll(() => { view.frame.style.height = Math.max(40, Math.min(height, 1600)) + 'px'; });
        }

        window.addEventListener('message', e => {
            for (const [viewId, view] of appViews) {
                if (e.source !== view.frame.contentWindow) continue;
                if (e.origin === view.origin && e.data && typeof e.data === 'object')
                    post({ type: 'mcpApp', view: viewId, message: e.data });
                return;
            }
        });

        function withStickyScroll(fn) {
            const stick = isNearBottom();
            fn();
            if (stick) scrollToBottom();
        }

        function appendMessage(id, role, html) {
            withStickyScroll(() => {
                const t = document.createElement('div');
                t.id = 'msg-' + id;
                t.className = 'turn ' + role;
                t.innerHTML =
                    "<div class='message " + role + "'>" +
                        "<div class='steps'></div><div class='content'></div>" +
                        "<div class='status' hidden><span class='dots'><i></i><i></i><i></i></span>" +
                        "<span class='status-text'></span><span class='elapsed'></span></div>" +
                    "</div><div class='meta'></div>";
                t.querySelector('.content').innerHTML = html;
                document.getElementById('messages').appendChild(t);
                renderContent(t.querySelector('.content'), true);
            });
        }

        function updateMessageContent(id, html, isFinal) {
            const t = turn(id);
            if (!t) return;
            withStickyScroll(() => {
                const el = t.querySelector('.content');
                const cached = new Map();
                el.querySelectorAll('[data-mermaid-hash]').forEach(n => {
                    const svg = n.querySelector('svg');
                    if (svg) cached.set(n.getAttribute('data-mermaid-hash'), svg.cloneNode(true));
                });
                el.innerHTML = html;
                el.querySelectorAll('[data-mermaid-hash]').forEach(n => {
                    const hash = n.getAttribute('data-mermaid-hash');
                    if (cached.has(hash)) {
                        n.innerHTML = '';
                        n.appendChild(cached.get(hash));
                        n.setAttribute('data-mermaid-rendered', 'true');
                    }
                });
                renderContent(el, isFinal);
            });
        }

        // Shows the animated "working" line with a live elapsed counter; pass '' to hide it.
        // The counter measures from the first status shown for this message. Hiding the line doesn't stop
        // the clock (the reply is still being written); paused=true does, e.g. while waiting for an approval.
        function setMessageStatus(id, text, paused) {
            const t = turn(id);
            if (!t) return;
            const status = t.querySelector('.status');
            let clock = statusTimers.get(id);
            if (!clock) { clock = { base: 0, start: performance.now(), handle: null }; statusTimers.set(id, clock); }
            const elapsedMs = () => clock.base + (clock.start === null ? 0 : performance.now() - clock.start);
            if (paused && clock.start !== null) { clock.base = elapsedMs(); clock.start = null; }
            if (!paused && clock.start === null) clock.start = performance.now();
            if (clock.handle) { clearInterval(clock.handle); clock.handle = null; }
            if (!text) { status.hidden = true; return; }
            withStickyScroll(() => {
                status.querySelector('.status-text').textContent = text;
                status.hidden = false;
            });
            const elapsed = status.querySelector('.elapsed');
            const tick = () => { elapsed.textContent = (elapsedMs() / 1000).toFixed(1) + 's'; };
            tick();
            if (clock.start !== null) clock.handle = setInterval(tick, 100);
        }

        // Adds or updates one step row (tool call / reasoning). state: running | done | error.
        function upsertMessageStep(id, stepId, html, state) {
            const t = turn(id);
            if (!t) return;
            withStickyScroll(() => {
                const steps = t.querySelector('.steps');
                let step = steps.querySelector("[data-step='" + CSS.escape(stepId) + "']");
                if (!step) {
                    step = document.createElement('div');
                    step.setAttribute('data-step', stepId);
                    steps.appendChild(step);
                }
                step.className = 'step ' + state;
                // Keep a <details> the user expanded open across updates.
                const wasOpen = step.querySelector('details')?.open;
                step.innerHTML = html;
                if (wasOpen) step.querySelector('details')?.setAttribute('open', '');
            });
        }

        function setMessageMeta(id, text, title, trace) {
            const t = turn(id);
            if (!t) return;
            withStickyScroll(() => {
                const meta = t.querySelector('.meta');
                meta.textContent = text;
                meta.title = title || '';
                // A recorded turn: the footer opens it in the observability window.
                if (trace) {
                    meta.classList.add('traced');
                    meta.onclick = () => post({ type: 'openTrace', trace });
                }
            });
        }

        async function renderContent(el, isFinal) {
            el.querySelectorAll('pre code:not(.hljs)').forEach(block => {
                if (typeof hljs !== 'undefined') hljs.highlightElement(block);
            });
            if (!isFinal || typeof mermaid === 'undefined') return;
            const unrendered = el.querySelectorAll('[data-mermaid-hash]:not([data-mermaid-rendered])');
            if (unrendered.length > 0) {
                try { await mermaid.run({ nodes: unrendered }); }
                catch (e) { console.warn('Mermaid render error:', e); }
            }
        }

        function post(message) {
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
        }

        // Files dragged onto the conversation become attachments: the page reads each dropped file and
        // hands its bytes to the host (which decides what can be attached). Nothing else may be dropped here.
        let dragDepth = 0;
        const hasFiles = e => Array.from(e.dataTransfer?.types || []).includes('Files');
        function setDropHint(text) {
            let hint = document.getElementById('drop-hint');
            if (!hint) { hint = document.createElement('div'); hint.id = 'drop-hint'; document.body.appendChild(hint); }
            hint.textContent = text;
        }
        document.addEventListener('dragenter', e => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            dragDepth++;
            document.body.classList.add('dragging');
        });
        document.addEventListener('dragover', e => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        });
        document.addEventListener('dragleave', e => {
            if (!hasFiles(e)) return;
            if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging'); }
        });
        document.addEventListener('drop', async e => {
            e.preventDefault();
            dragDepth = 0;
            document.body.classList.remove('dragging');
            for (const file of Array.from(e.dataTransfer?.files || [])) {
                if (file.size > 20 * 1024 * 1024) { post({ type: 'dropTooLarge', name: file.name }); continue; }
                const bytes = new Uint8Array(await file.arrayBuffer());
                let binary = '';
                for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
                post({ type: 'dropFile', name: file.name, data: btoa(binary) });
            }
        });

        // Text size: the host owns the level (shared with the composer and remembered); this page only
        // applies it and forwards the shortcuts it receives while focused (Ctrl + / - / 0, Ctrl + wheel).
        const baseFontPx = parseFloat(getComputedStyle(document.body).fontSize);
        function setTextZoom(percent) {
            withStickyScroll(() => { document.body.style.fontSize = (baseFontPx * percent / 100) + 'px'; });
        }
        document.addEventListener('keydown', e => {
            if (!e.ctrlKey || e.altKey || e.metaKey) return;
            const step = (e.key === '+' || e.key === '=') ? 1 : e.key === '-' ? -1 : e.key === '0' ? 0 : null;
            if (step === null) return;
            e.preventDefault();
            post({ type: 'zoom', step });
        });
        window.addEventListener('wheel', e => {
            if (!e.ctrlKey) return;
            e.preventDefault();
            post({ type: 'zoom', step: e.deltaY < 0 ? 1 : -1 });
        }, { passive: false });

        function openArtifactFrom(target) {
            const card = target.closest('.artifact-card[data-artifact]');
            if (card) post({ type: 'openArtifact', id: card.dataset.artifact });
            return !!card;
        }

        document.addEventListener('keydown', e => {
            if ((e.key === 'Enter' || e.key === ' ') && openArtifactFrom(e.target)) e.preventDefault();
        });

        document.addEventListener('click', e => {
            if (openArtifactFrom(e.target)) return;
            // Tool approval buttons (only ever rendered by the host; Markdown output can't contain HTML).
            const button = e.target.closest('.approval button[data-decision]');
            if (button) {
                const card = button.closest('.approval');
                card.querySelectorAll('button').forEach(b => b.disabled = true);
                card.classList.add('decided');
                post({ type: 'toolApproval', callId: card.dataset.call, decision: button.dataset.decision });
                return;
            }
            // Links open in the user's browser instead of navigating the chat away.
            const link = e.target.closest('a[href]');
            if (link) {
                e.preventDefault();
                if (!link.getAttribute('href').startsWith('#')) post({ type: 'openLink', href: link.href });
            }
        });
        </script>
        """.Replace("__NONCE__", nonce);
}
