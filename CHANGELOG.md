# Harness.WinUI - Changelog

## [1.1.1] - 2026-10-18

### Added
- Conversation width (Settings → General → Appearance): narrow, standard, wide or the whole window
- Ask the agent to change how the app looks ("make the conversation wider", "bigger text", "hide the usage button"): text
  size, conversation width and which toolbar buttons show. It asks you every time, and can't change anything else
- Tips on an empty conversation about what Harness.WinUI can do for you, one at a time. Close each one, or turn them off in
  Settings → General → Appearance

### Changed
- The conversation column is wider by default (it was a fixed 900 px; it is now 84 times the text size, which is about
  1176 px, and grows with the text size), so a big window has less empty space on the sides. Smaller windows look the
  same as before. Choose "Narrow" for about the old width

## [1.1.0] - 2026-10-14

### Added
- Long-term memory (Settings → Memory, off by default): the agent can save short notes about you and your work, find them
  again, update or forget them. Every note is approved by you first, and nothing that looks like a key or a password is
  ever saved. The notes are Markdown files in a `memory` folder (you can read and edit them), each remembering which
  conversation it came from. At the start of a conversation the most relevant ones are put in front of the model, chosen
  once so the prompt cache keeps working. Notes unused for 90 days (adjustable) stop being used until you bring them back
- Memory window (toolbar): your memories as a list (like other assistants' "manage memory": what, where it came from, how
  often it was used), as a timeline (when, and in which conversation, something was remembered), and as a connection
  graph for exploring what belongs together. Search covers memories and past conversations; click a memory to edit it,
  forget it (with undo) or see where it came from, and click its conversation to open it
- Under a conversation's first reply, "Used N memories" shows which memories the agent was given; click it to see them
- Tidy up ("整理記憶"): your model looks at the notes and at recent conversations and suggests what to add, merge, rewrite, drop,
  relate or confirm. You see how much will be sent first, then pick which suggestions to apply; replaced and dropped notes
  are kept so they can be restored
- Search past conversations (Settings → General, off by default): the agent can look through your saved conversations
  to answer "when did I work on this, and in which conversation?". Each search asks for your approval first (read-only
  mode doesn't block it, since it changes nothing), because what it finds is sent to your model. The answer links to
  the conversation; clicking the link opens it and jumps to that message

## [1.0.11] - 2026-10-11

### Added
- Several model connections: Settings now keeps a list (for example a cloud service and a local Ollama) with Add and
  Delete. The one showing when you press Save is the one in use. Your existing connection is the first entry; the file
  still holds the active one in the old place, so an older version keeps working
- Your own instructions for the model: a box in Settings (added to the built-in instructions in every conversation),
  and an AGENTS.md at the root of the sandbox folder (for work in that folder). Both are read on every message, so a
  change applies right away. The agent can't change AGENTS.md itself, so a page or file that talked it into rewriting
  its instructions can't make them stick across conversations
- Statistics → Day by day: a bar per day for the cost (or the tokens, when no prices are set) and for the model calls,
  with the busiest day named. Days without activity show as gaps

### Changed
- Settings is split into pages (Connection, Pricing, General, Permissions) instead of one long scroll; a problem found
  on saving takes you to the page it is on
- README: a local OpenAI-compatible server such as Ollama can be added as a connection. Ollama's default context length
  (4096 tokens) is too small for tools, so raise it (`OLLAMA_CONTEXT_LENGTH=16384`)

### Removed
- More menu → About Harness.WinUI (added in 1.0.10): it was in the way more than it helped

## [1.0.10] - 2026-10-09

### Added
- Read-only mode: a lock button in the toolbar. While it's on, the agent can read and search but anything that
  changes files or runs commands is refused (including tools an MCP App calls), whatever was allowed before. The
  model is told when you switch it, so it doesn't keep answering as if it were still on. It isn't remembered across
  launches
- "Allow for this chat" on the approval card: the same action runs without asking until you start a new chat or open
  another one. It is never saved; "Always allow" still is
- Retry: when a reply fails or you stop it, a Retry button under it sends the same message again, with the agent back
  where it was before that message
- Shortcuts: Ctrl+N starts a new chat and Ctrl+H opens the history
- More menu → About Harness.WinUI: the version, that the app is open source (MIT), and links to the source code,
  where to report an issue, the privacy policy and the third-party notices

## [1.0.9] - 2026-10-06

### Added
- Copy buttons: one on every code block (copies just the code) and one under each reply (copies the
  reply's text), shown when you point at them
- Skills from the composer: type `/` at the start of a message to list your skills (filtered as you type),
  pick one with the arrow keys and Enter or Tab, or a click, then write your request. The agent is told to
  use that skill; the conversation shows just what you typed
- History: a search box (finds a conversation by its title or anything said in it), and a "⋯" menu on
  each conversation to rename it or export it as a Markdown file

### Changed
- Observability → Statistics shows the prompt-cache hit rate (overall and per model), and its labels now
  explain themselves: hover a card for what it counts, and each table has a short note on its columns.
  The AI analysis also looks at the cache hit rate

## [1.0.8] - 2026-10-04

### Fixed
- The Store package now declares English and Traditional Chinese, the languages Harness.WinUI's interface
  is in, instead of every language the bundled WinUI components have text for (the Store page listed 86).
  WinUI's own text, such as context menus and tooltips, is in Chinese on a Traditional Chinese system and
  in English otherwise

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
