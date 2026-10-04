# Privacy Policy

**Effective Date:** 2026-10-04

**Harness.WinUI** is a desktop AI assistant for Windows. It connects to an AI model service that **you**
configure, and it runs entirely on your device otherwise.

## What we collect

Nothing. The developer of Harness.WinUI does not run any servers for the app, and Harness.WinUI contains no
analytics, telemetry, advertising or tracking. The developer never receives your messages, files,
settings or API keys.

## Data sent to services you configure

To answer you, Harness.WinUI sends data to the services you set up yourself:

- **Your AI model endpoint** (for example Azure OpenAI, OpenAI, or a LiteLLM gateway) receives your
  messages, the conversation so far, the images and files you attach, and whatever tools return
  during the conversation — for example the content of a file you ask Harness.WinUI to read, or a
  skill it loads. The optional image model receives your image prompts.
- **MCP servers you add** receive the tool calls the assistant makes to them.
- **Web search** (optional, off by default): when you turn it on, your model provider runs the searches
  the assistant asks for with its own search service (on Azure OpenAI, Grounding with Bing, which is
  outside Azure's data boundary).
- **Websites the assistant reads** (`fetch_url`) receive an ordinary request from your PC for that page.
  Addresses on your PC or internal network are only fetched after you approve.
- **An OpenTelemetry backend you set up** (optional, off by default) receives the observability
  records described below — with message content only if you chose to record it.

How those services handle your data is governed by their own terms and privacy policies. Harness.WinUI does
not send data anywhere else.

## Data stored on your device

- **Settings** (endpoint, model, language, preferences), **conversation history** and **token
  usage records** are stored locally in the app's data folder.
- **API keys and MCP authentication secrets** are encrypted with Windows Data Protection (DPAPI)
  and can only be decrypted by your Windows user account. They are never stored in plain text.
- **Generated files** (documents, images, artifacts) are saved where the app shows you: the sandbox
  folder you choose, and `Pictures\Harness.WinUI` for images.
- **Observability records** (only if you turn recording on): what was sent to your model and what came
  back, tool calls and their results, timing, tokens and cost, kept in the app's data folder for the
  number of days you choose (30 by default). You can leave message content out, or delete all records.
  When you use AI analysis, the records it reads are sent to your model endpoint.

You can delete conversations in the app at any time. Uninstalling the Microsoft Store version
removes its app data folder.

## File access

Harness.WinUI's file tools only work inside the sandbox folder you choose (or can be turned off in
Settings). Actions that change files or Harness.WinUI's configuration ask for your approval first.

Commands the assistant runs (`run_command`) start in the sandbox folder and run on your PC with your
Windows account's permissions, like a terminal you open yourself. Each one is shown to you in full and
runs only after you approve it.

## Changes

If this policy changes, the updated version will be published at this location with a new
effective date.

## Contact

Questions about this policy: breezy8975757@gmail.com

---

# 隱私權政策（繁體中文）

**生效日期：** 2026-10-04

**Harness.WinUI** 是 Windows 上的 AI 助手。它只會連到**你自己設定**的 AI 模型服務，其餘所有動作都在你的電腦上完成。

- **我們不蒐集任何資料**：開發者沒有為 Harness.WinUI 架設任何伺服器；App 內沒有分析、遙測、廣告或追蹤。開發者不會收到你的訊息、檔案、設定或 API key。
- **傳送給你設定的服務**：你的訊息、對話內容、你附加的圖片和檔案，以及對話中工具的結果（例如你請 Harness.WinUI 讀取的檔案內容、它載入的技能），會送到你設定的模型端點（如 Azure OpenAI、OpenAI、LiteLLM）；生圖提示會送到你設定的生圖模型；你加入的 MCP server 會收到對它的工具呼叫；網路搜尋（選用，預設關閉）開啟後，由你的模型供應商用它自己的搜尋服務執行（Azure OpenAI 為 Bing，在 Azure 資料邊界之外）；助手讀取的網站（`fetch_url`）會收到一般的網頁請求，本機或內網位址需經你核准才會讀取；你自行設定的 OpenTelemetry 後台（選用，預設關閉）會收到下述觀測紀錄，只有在你選擇記錄訊息內容時才包含內容。這些服務如何處理資料，依其各自的條款與隱私權政策。
- **存在你電腦上的資料**：設定、對話紀錄與 token 用量紀錄存在 App 的本機資料夾。API key 與 MCP 驗證密鑰使用 Windows DPAPI 加密，只有你的 Windows 帳號能解開，不會以明碼存放。產生的檔案存在你指定的沙盒資料夾，圖片存在 `圖片\Harness.WinUI`。觀測紀錄只在你開啟記錄時產生：送給模型的內容與回應、工具呼叫與結果、時間、token 與費用，存在 App 的本機資料夾，保留你設定的天數（預設 30 天），可以選擇不記錄訊息內容或全部刪除；使用 AI 分析時，它讀取的紀錄會送到你的模型端點。
- **檔案存取**：檔案工具只能在你選擇的沙盒資料夾內運作（也可以在設定中關閉）；會修改檔案或 Harness.WinUI 設定的動作，都會先請你核准。助手執行的指令（`run_command`）從沙盒資料夾開始，在你的電腦上以你的 Windows 帳號權限執行，就像你自己開的終端機；每個指令都會完整顯示給你，經你核准後才會執行。
- **聯絡方式**：breezy8975757@gmail.com
