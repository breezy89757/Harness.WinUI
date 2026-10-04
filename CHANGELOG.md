# Harness.WinUI - Changelog

## [1.0.7] - 2026-10-04

### Added
- `run_command`: the agent can run PowerShell commands in the sandbox folder (PowerShell 7 when it's
  installed), e.g. run a script it wrote, build or test code. Every command is shown in full on an
  approval card and runs only after you approve it. Input is closed, so a program can't hang waiting
  for typing; output is UTF-8 and cut to its start and end when long; commands stop after 2 minutes by
  default (up to 10), and the whole process tree is stopped on timeout or when you press Stop
- "Always allow" for commands covers commands that start the same way: the program and its subcommand,
  e.g. `uv run`, `git status`, `python`. Commands that chain, pipe, redirect or use `$`, and ones that
  start a shell, delete files or start detached programs, are asked every time
- Settings → Always allowed: see every action that runs without asking (file tools, MCP tools, command
  prefixes) and remove any of them; removals apply when you save

## [1.0.6] - 2026-10-04

### Added
- `fetch_url`: the agent reads a web page (main content as Markdown, via Mozilla Readability), or a PDF or
  Office file at a URL. Public sites need no approval; addresses on this PC or the internal network do,
  every time (checked on the address actually connected to, so redirects and DNS tricks can't get around it)
- Web search (off by default; turn it on in the response quality menu): your OpenAI or Azure OpenAI provider's built-in search, with the
  pages it used listed under the reply. On Azure OpenAI it uses Grounding with Bing, which is billed
  separately and is outside Azure's data boundary
- The working line under a reply says what's happening (searching the web, reading a page, running a
  tool, waiting for the model) instead of always "Thinking"
- Customize toolbar: tick several buttons and apply them at once

### Changed
- Built on .NET 10 (LTS); .NET 9 support ends in November 2026

## [1.0.5] - 2026-10-03

### Added
- Observability window (toolbar): see what is sent to the model and what comes back. Recording is off
  until you turn it on there; records stay on this PC (`trace.db`) and are kept 30 days by default
  - Log: every turn with its model, model and tool calls, tokens, cost and duration; a timeline of each
    turn (model calls, tool calls, approval waits); for each step the messages as the model saw them,
    the raw HTTP request and response, and every attribute. A recorded reply's footer opens its turn
  - Statistics: per model (median and P95 duration, time to first token, tokens, cost), per tool
    (offered vs. called, failures, time, result size), per skill, MCP server and approval decision
  - AI analysis: ask your model about the records ("which step is slowest?"); it reads them through
    read-only tools, and its own calls aren't recorded
  - OTLP export (optional, off until you set it up): send the records to Aspire Dashboard, Langfuse, an
    OpenTelemetry Collector or similar; headers are kept encrypted. Raw HTTP exchanges stay local
- Built on Microsoft.Extensions.AI's OpenTelemetry instrumentation (GenAI semantic conventions); with
  recording off it adds no work

### Fixed
- With the artifact panel open (or a narrow window) the toolbar squeezed the message box to a sliver;
  buttons that don't fit now move into the More (…) menu until there's room
- Tables in replies had no borders; they now have a frame, header divider, row lines and shading

## [1.0.4] - 2026-10-02

### Added
- Attachments: paste a screenshot or copied files into the message box (Ctrl+V), or drag files onto the
  window. Images are sent to the model as images; text, Office and PDF files as text. Up to 10 per message
- Cost: each reply shows its token cost; a usage button shows totals for the conversation, today, this
  month and all time. Set prices per 1M tokens (and the currency) in Settings
- Notifications: when Harness.WinUI is in the background, a Windows notification and a flashing taskbar
  button tell you a reply finished or a tool call is waiting for approval
- Agent Skills: folders with a `SKILL.md` (in Harness.WinUI's skills folder, `~/.agents/skills`,
  `~/.claude/skills` or the sandbox's `.agents/skills`) teach the agent a kind of task; it loads one when a
  request matches. Turn skills on or off in the MCP & tools dialog
- Text size: Ctrl + plus / minus / 0, or Ctrl + mouse wheel, for the conversation and the message box
- Customizable toolbar: right-click it to choose which buttons show; the rest are in the More (…) menu

### Fixed
- Turning an MCP server off or on in the dialog didn't take effect
- The reply timer kept running while a tool call waited for your approval
- Opening a second dialog while one was open could close the app

## [1.0.3] - 2026-10-01

### Added
- MCP Apps (`io.modelcontextprotocol/ui`, spec 2026-01-26): a tool's `ui://` view is shown in the chat
  under its step, in a sandboxed frame on its own origin with the CSP the view declares
  - Views can call their server's tools (app-only tools are hidden from the model; others need
    approval unless read-only), read its resources, open links, and send messages to the chat
  - The model gets a tool's text `content` only; `structuredContent` goes to the view
  - `ui/update-model-context`: what the user does in a view is added to their next message

## [1.0.2] - 2026-09-30

### Changed
- Remote MCP servers can use plain `http://` on the internal network, not just on this PC: allowed
  when the host resolves to a private address (10.x, 172.16-31.x, 192.168.x, link-local, IPv6 ULA).
  Servers on the public internet still need `https://`

## [1.0.1] - 2026-09-29

### Fixed
- The Microsoft Store version closed right after launch: its package was missing the app's XAML resources

### Changed
- Renamed throughout from the working name "Yoke": the executable is now `Harness.WinUI.exe`, and the
  portable build keeps its data in `%LOCALAPPDATA%\Harness.WinUI` (an existing `%LOCALAPPDATA%\Yoke`
  folder is moved there on first launch, with settings, keys and history)
- Developer config: the appsettings section is now `Harness` and environment variables use the
  `HARNESS_` prefix (e.g. `HARNESS_PROVIDER_APIKEY`)

## [1.0.0] - 2026-09-27

First public release.

### Agent
- Chat with any OpenAI-compatible endpoint (Azure OpenAI, OpenAI, LiteLLM); official endpoints use
  the Responses API, others Chat Completions
- Streaming replies with reasoning summaries, tool-call steps, token usage, model and timing
- Stop a reply at any time (Stop button or Esc); reasoning effort and image quality settings
- Traditional Chinese and English UI; replies follow the user's language (Taiwan Traditional Chinese
  for Chinese)

### Tools
- Built-in file tools limited to a sandbox folder: list, read, find, search, write, edit, move
- Reads text from .docx, .xlsx, .pptx and .pdf; creates real Word and Excel files
- MCP servers (stdio and Streamable HTTP), managed in the app or added by the agent on request
- Image generation with gpt-image models
- Approval prompts for anything that changes files or Harness.WinUI's configuration

### Workspace
- Live artifact panel for HTML, SVG, Mermaid and Markdown, with versions, save and open in browser
- Conversation history stored locally (SQLite), resumable with the model's context

### Privacy & packaging
- API keys and MCP secrets encrypted with Windows DPAPI
- MSIX package for the Microsoft Store (x64 and ARM64)
