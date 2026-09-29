# Harness.WinUI - Changelog

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
