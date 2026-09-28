# Privacy Policy

**Effective Date:** 2026-09-27

**Harness.WinUI** is a desktop AI assistant for Windows. It connects to an AI model service that **you**
configure, and it runs entirely on your device otherwise.

## What we collect

Nothing. The developer of Harness.WinUI does not run any servers for the app, and Harness.WinUI contains no
analytics, telemetry, advertising or tracking. The developer never receives your messages, files,
settings or API keys.

## Data sent to services you configure

To answer you, Harness.WinUI sends data to the services you set up yourself:

- **Your AI model endpoint** (for example Azure OpenAI, OpenAI, or a LiteLLM gateway) receives your
  messages, the conversation so far, and whatever tools return during the conversation — for
  example the content of a file you ask Harness.WinUI to read. The optional image model receives your image
  prompts.
- **MCP servers you add** receive the tool calls the assistant makes to them.

How those services handle your data is governed by their own terms and privacy policies. Harness.WinUI does
not send data anywhere else.

## Data stored on your device

- **Settings** (endpoint, model, language, preferences) and **conversation history** are stored
  locally in the app's data folder.
- **API keys and MCP authentication secrets** are encrypted with Windows Data Protection (DPAPI)
  and can only be decrypted by your Windows user account. They are never stored in plain text.
- **Generated files** (documents, images, artifacts) are saved where the app shows you: the sandbox
  folder you choose, and `Pictures\Harness.WinUI` for images.

You can delete conversations in the app at any time. Uninstalling the Microsoft Store version
removes its app data folder.

## File access

Harness.WinUI's file tools only work inside the sandbox folder you choose (or can be turned off in
Settings). Actions that change files or Harness.WinUI's configuration ask for your approval first.

## Changes

If this policy changes, the updated version will be published at this location with a new
effective date.

## Contact

Questions about this policy: breezy8975757@gmail.com

---

# 隱私權政策（繁體中文）

**生效日期：** 2026-09-27

**Harness.WinUI** 是 Windows 上的 AI 助手。它只會連到**你自己設定**的 AI 模型服務，其餘所有動作都在你的電腦上完成。

- **我們不蒐集任何資料**：開發者沒有為 Harness.WinUI 架設任何伺服器；App 內沒有分析、遙測、廣告或追蹤。開發者不會收到你的訊息、檔案、設定或 API key。
- **傳送給你設定的服務**：你的訊息、對話內容，以及對話中工具的結果（例如你請 Harness.WinUI 讀取的檔案內容），會送到你設定的模型端點（如 Azure OpenAI、OpenAI、LiteLLM）；生圖提示會送到你設定的生圖模型；你加入的 MCP server 會收到對它的工具呼叫。這些服務如何處理資料，依其各自的條款與隱私權政策。
- **存在你電腦上的資料**：設定與對話紀錄存在 App 的本機資料夾。API key 與 MCP 驗證密鑰使用 Windows DPAPI 加密，只有你的 Windows 帳號能解開，不會以明碼存放。產生的檔案存在你指定的沙盒資料夾，圖片存在 `圖片\Harness.WinUI`。
- **檔案存取**：檔案工具只能在你選擇的沙盒資料夾內運作（也可以在設定中關閉）；會修改檔案或 Harness.WinUI 設定的動作，都會先請你核准。
- **聯絡方式**：breezy8975757@gmail.com
