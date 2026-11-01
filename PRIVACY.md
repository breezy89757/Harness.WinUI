# Privacy Policy

**Effective Date:** 2026-10-23

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
  skill it loads. In Code mode this includes the project files it reads and the output of commands
  you approve. The optional image model receives your image prompts.
- **Company themes for presentations** (optional): when you make a theme from a PowerPoint template,
  Harness.WinUI sends your model what it found in the template (layout names, sizes, fonts and the sample
  text on the template's own slides) so the model can write a short design guide. That guide is then
  included in the instructions sent with later presentation requests.
- **MCP servers you add** receive the tool calls the assistant makes to them.
- **Web search** (optional, off by default): when you turn it on, your model provider runs the searches
  the assistant asks for with its own search service (on Azure OpenAI, Grounding with Bing, which is
  outside Azure's data boundary).
- **Operating other programs** (optional, off by default): when you allow a use, the desktop agent sends your model a screenshot of that one
  window and the text and controls Windows reports for it (the text is also read with Windows' own offline OCR), on every step, so it can
  see what to click. Each use asks for your approval first and names the window and the task; the app's own window is never used.
  A separate tool can list the titles and programs of your open windows so the next window can be named; it asks for your approval every time.
- **Running web tasks in a browser** (optional, off by default): when you allow a use, the browser agent opens the site in a browser window and
  sends your model, on every step, the page's address, title and text structure, the elements it can act on, and a screenshot of the page. Each
  use asks for your approval first and names the address and the task. Passwords and other secrets you store are kept encrypted for your Windows
  account and are never sent to the model (only a placeholder is); password fields are covered in the screenshot. A recording of what worked
  (addresses, element names and the text it checked, never a secret's value) is kept on your PC so it can be replayed; screenshots are kept only if
  you choose a folder for them. The browser is shown in a pane in the app; for each run it is a fresh browser profile (deleted when the pane is closed, so
  nothing stays signed in), and it listens on a random port on 127.0.0.1 only, so that the app can drive it; another program on your PC could connect to
  that port while the pane is open.
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
  folder you choose, and `Pictures\Harness.WinUI` for images. A presentation you export is saved as a
  `.pptx` file where you choose.
- **Company themes** you create (a copy of your PowerPoint template, the logos and pictures inside it, and
  the design guide the model wrote) are stored only in the app's data folder, and can be deleted from
  the theme menu. Task lists and presentations are saved with the conversation they belong to.
- **Observability records** (only if you turn recording on): what was sent to your model and what came
  back, tool calls and their results, timing, tokens and cost, kept in the app's data folder for the
  number of days you choose (30 by default). You can leave message content out, or delete all records.
  When you use AI analysis, the records it reads are sent to your model endpoint.

You can delete conversations in the app at any time. Uninstalling the Microsoft Store version
removes its app data folder.

## File access

Harness.WinUI's file tools only work inside the sandbox folder you choose (or can be turned off in
Settings). In Code mode that folder is the project folder you pick. Actions that change files or
Harness.WinUI's configuration ask for your approval first, and a reply that changed files can be undone.

Commands the assistant runs (`run_command`) start in the sandbox folder and run on your PC with your
Windows account's permissions, like a terminal you open yourself. Each one is shown to you in full and
runs only after you approve it, unless you chose to always allow commands that start the same way (e.g.
`uv run`) or turned on YOLO mode, which stops asking for files and commands until you turn it off or close
the app. You can review and remove what's always allowed in Settings. In Code mode the assistant can also
start programs that keep running (such as a development server) under the same rules; they are stopped
when you close the app.

## Changes

If this policy changes, the updated version will be published at this location with a new
effective date.

## Contact

Questions about this policy: breezy8975757@gmail.com

---

# 隱私權政策（繁體中文）

**生效日期：** 2026-10-23

**Harness.WinUI** 是 Windows 上的 AI 助手。它只會連到**你自己設定**的 AI 模型服務，其餘所有動作都在你的電腦上完成。

- **我們不蒐集任何資料**：開發者沒有為 Harness.WinUI 架設任何伺服器；App 內沒有分析、遙測、廣告或追蹤。開發者不會收到你的訊息、檔案、設定或 API key。
- **傳送給你設定的服務**：你的訊息、對話內容、你附加的圖片和檔案，以及對話中工具的結果（例如你請 Harness.WinUI 讀取的檔案內容、它載入的技能；程式碼模式下還包括它讀取的專案檔案與你核准的指令輸出），會送到你設定的模型端點（如 Azure OpenAI、OpenAI、LiteLLM）；你用 PowerPoint 範本建立公司主題時，範本裡找到的內容（版面名稱、尺寸、字型，以及範本自己投影片上的範例文字）會送到你的模型，請它寫一份簡短的設計指南，之後的簡報請求會把這份指南一併送出；生圖提示會送到你設定的生圖模型；你加入的 MCP server 會收到對它的工具呼叫；網路搜尋（選用，預設關閉）開啟後，由你的模型供應商用它自己的搜尋服務執行（Azure OpenAI 為 Bing，在 Azure 資料邊界之外）；操作其他程式（選用，預設關閉）：你同意某一次使用後，桌面 agent 每一步都會把那一個視窗的截圖，以及 Windows 回報的文字與控制項（文字也會用 Windows 內建、離線的 OCR 讀取）送到你的模型，讓它知道要點哪裡；每次使用都會先問你，並寫出視窗與任務，本程式自己的視窗不會被使用；另有一個工具可以列出你開著的視窗的標題與程式名稱，好讓下一個視窗能被指名，它每次都會先問你；在瀏覽器裡執行網頁任務（選用，預設關閉）：你同意某一次使用後，瀏覽器 agent 會在瀏覽器視窗開啟網站，並在每一步把頁面的網址、標題與文字結構、它能操作的元素，以及頁面截圖送到你的模型；每次使用都會先問你並寫出網址與任務。你存的密碼與其他秘密值以你的 Windows 帳號加密保存，不會送給模型（只送佔位字串），截圖裡的密碼欄位會被蓋住。做成功的步驟會存成錄製檔留在你的電腦上，好讓它可以重跑（內容是網址、元素名稱與它檢查的文字，不含秘密值）；截圖只有在你指定資料夾時才會保存；瀏覽器會顯示在程式裡的窗格，每次使用都是全新的瀏覽器設定檔（關閉窗格時刪除，所以不會留下任何登入狀態），並只在 127.0.0.1 的隨機連接埠上接受程式的操作，窗格開著的期間，你電腦上的其他程式理論上可以連上那個連接埠；助手讀取的網站（`fetch_url`）會收到一般的網頁請求，本機或內網位址需經你核准才會讀取；你自行設定的 OpenTelemetry 後台（選用，預設關閉）會收到下述觀測紀錄，只有在你選擇記錄訊息內容時才包含內容。這些服務如何處理資料，依其各自的條款與隱私權政策。
- **存在你電腦上的資料**：設定、對話紀錄與 token 用量紀錄存在 App 的本機資料夾。API key 與 MCP 驗證密鑰使用 Windows DPAPI 加密，只有你的 Windows 帳號能解開，不會以明碼存放。產生的檔案存在你指定的沙盒資料夾，圖片存在 `圖片\Harness.WinUI`；匯出的簡報是你指定位置的 `.pptx` 檔。你建立的公司主題（你的 PowerPoint 範本複本、其中的標誌與圖片，以及模型寫的設計指南）只存在 App 的本機資料夾，可從主題選單刪除；任務清單與簡報會跟著所屬的對話一起儲存。觀測紀錄只在你開啟記錄時產生：送給模型的內容與回應、工具呼叫與結果、時間、token 與費用，存在 App 的本機資料夾，保留你設定的天數（預設 30 天），可以選擇不記錄訊息內容或全部刪除；使用 AI 分析時，它讀取的紀錄會送到你的模型端點。
- **檔案存取**：檔案工具只能在你選擇的沙盒資料夾內運作（也可以在設定中關閉；程式碼模式下就是你選的專案資料夾）；會修改檔案或 Harness.WinUI 設定的動作，都會先請你核准，改過檔案的回覆也可以復原。助手執行的指令（`run_command`）從沙盒資料夾開始，在你的電腦上以你的 Windows 帳號權限執行，就像你自己開的終端機；每個指令都會完整顯示給你，經你核准後才會執行，除非你選擇永遠允許以相同方式開頭的指令（例如 `uv run`）。你也可以開啟 YOLO 模式，在你關閉它或關閉 App 之前不再詢問檔案與指令。永遠允許的項目可以在設定中查看及移除。程式碼模式下助手也可以啟動持續執行的程式（例如開發伺服器），規則相同，關閉 App 時會一併停止。
- **聯絡方式**：breezy8975757@gmail.com
