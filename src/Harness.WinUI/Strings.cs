// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>
/// UI strings in English and Traditional Chinese. Two languages don't justify .resw/PRI plumbing in an
/// unpackaged app; a table here is also usable from the ViewModel and the HTML it generates. The
/// language is chosen once at startup (changing it needs a restart). XAML binds with
/// <c>{x:Bind local:Strings.Name}</c>.
/// </summary>
public static class Strings
{
    public static bool IsChinese { get; private set; }

    /// <param name="preference">"system", "en" or "zh-TW" (see <see cref="AppPreferences.Language"/>).</param>
    public static void Initialize(string? preference)
    {
        IsChinese = preference switch
        {
            AppPreferences.TraditionalChinese => true,
            AppPreferences.English => false,
            _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string T(string en, string zh) => IsChinese ? zh : en;

    // Main window
    public static string ChatTranscript => T("Chat transcript", "對話內容");
    public static string SettingsTooltip => T("Settings", "設定");
    public static string ToolsTooltip => T("MCP servers and tools", "MCP 伺服器與工具");
    public static string MessagePlaceholder => T("Message Harness.WinUI…", "傳訊息給 Harness.WinUI…");
    public static string MessageInput => T("Message input", "訊息輸入框");
    public static string Send => T("Send", "送出");
    public static string SendMessage => T("Send message", "送出訊息");
    public static string NewChat => T("New chat", "新對話");
    public static string History => T("History", "對話紀錄");
    public static string NoHistory => T("No saved conversations yet.", "還沒有對話紀錄。");
    public static string DeleteConversation => T("Delete conversation", "刪除對話");
    public static string ResumedContextLost => T(
        "The earlier context of this conversation couldn't be restored (the provider may have expired it), so the next message starts fresh. The transcript above is still saved.",
        "無法接續這段對話先前的內容（模型服務端的紀錄可能已過期），下一則訊息會從新的上下文開始。上方的對話紀錄仍然保留。");

    public static string RelativeTime(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.UtcNow - time;
        if (elapsed < TimeSpan.FromMinutes(1))
            return T("just now", "剛剛");
        if (elapsed < TimeSpan.FromHours(1))
            return T($"{(int)elapsed.TotalMinutes} min ago", $"{(int)elapsed.TotalMinutes} 分鐘前");
        if (elapsed < TimeSpan.FromDays(1))
            return T($"{(int)elapsed.TotalHours} h ago", $"{(int)elapsed.TotalHours} 小時前");
        if (elapsed < TimeSpan.FromDays(2))
            return T("yesterday", "昨天");
        return time.ToLocalTime().ToString(IsChinese ? "M/d" : "MMM d", IsChinese ? CultureInfo.GetCultureInfo("zh-TW") : CultureInfo.GetCultureInfo("en-US"));
    }

    // Chat status and steps
    public static string Thinking => T("Thinking…", "思考中…");
    public static string WaitingForModel => T("Waiting for the model…", "正在等模型回應…");
    public static string ModelSlow => T("The model is slow to respond — still waiting (Stop to cancel)…", "模型回應較慢，仍在等待…（可按停止）");
    public static string ReadingPage(string host) => T($"Reading {host}…", $"正在讀取 {host}…");
    public static string WaitingForApproval => T("Waiting for your approval…", "等待你的核准…");
    public static string RunningTool(string name) => T($"Running {name}…", $"正在執行 {name}…");
    public static string GeneratingImage(string quality, string estimate) => T($"Generating image ({quality}, {estimate})…", $"正在生成圖片（{quality}，{estimate}）…");
    public static string AboutTwoMinutes => T("~2 min", "約 2 分鐘");
    public static string AboutSeconds(int seconds) => T($"~{seconds}s", $"約 {seconds} 秒");
    public static string Reasoning => T("Reasoning", "思考過程");
    public static string Result => T("Result", "結果");
    public static string Declined => T("declined", "已拒絕");
    public static string AllowThisAction => T("Allow this action?", "允許執行這個動作嗎？");
    public static string AllowOnce => T("Allow once", "允許一次");
    public static string AlwaysAllow => T("Always allow", "永遠允許");
    public static string AlwaysAllowStartingWith(string prefix) => T($"Always allow “{prefix} …”", $"永遠允許「{prefix} …」");
    public static string AlwaysAllowedHeader => T("Always allowed", "永遠允許的動作");
    public static string AlwaysAllowedHelp => T(
        "Actions you chose “Always allow” for run without asking. Remove one to be asked again (applies when you save).",
        "你選擇「永遠允許」的動作，執行前不會再詢問。移除後會重新詢問（按「儲存」後生效）。");
    public static string AlwaysAllowedNone => T("Nothing is always allowed.", "目前沒有永遠允許的動作。");
    public static string CommandsStartingWith(string prefix) => T($"Commands starting with “{prefix}”", $"開頭為「{prefix}」的指令");
    public static string CommandWhere => T("PowerShell, in the sandbox folder, with your permissions", "PowerShell，在沙盒資料夾中、以你的權限執行");
    public static string CommandTimeout(string? seconds) => T($"stops after {seconds} s", $"{seconds} 秒後停止");
    public static string RunningCommand => T("Running a command…", "正在執行指令…");
    public static string Deny => T("Deny", "拒絕");
    public static string Stop => T("Stop", "停止");
    public static string StopTooltip => T("Stop generating (Esc)", "停止回應（Esc）");
    public static string Stopped => T("Stopped", "已停止");
    public static string StoppedTooltip => T(
        "Stopped by you. This turn isn't part of the model's context — resend it (or ask again) to continue.",
        "已手動停止。這一輪不會納入模型的上下文，要繼續請重新傳送或再問一次。");
    public static string ResponseQuality => T("Response quality", "回應品質");
    public static string ReasoningEffortHeader => T("Reasoning effort", "推理強度");
    public static string EffortAuto => T("Auto (model default)", "自動（模型預設）");
    public static string EffortLow => T("Low — fastest", "低 — 最快");
    public static string EffortMedium => T("Medium", "中");
    public static string EffortHigh => T("High — most thorough", "高 — 最仔細");
    public static string ImageQualityHeader => T("Image quality", "生圖品質");
    public static string ImageLow => T("Low — about 20s", "低 — 約 20 秒");
    public static string ImageMedium => T("Medium — about 50s", "中 — 約 50 秒");
    public static string ImageHigh => T("High — about 2 min", "高 — 約 2 分鐘");
    public static string WebSearch => T("Web search", "網路搜尋");
    public static string WebSearchUnsupported => T("Web search (needs the Responses API)", "網路搜尋（需要 Responses API）");
    public static string WebSearchNote => T(
        "The model can search the web with your provider's search (Bing on Azure). Searches are billed separately, and on Azure the queries leave Azure's data boundary.",
        "讓模型用供應商內建的搜尋查網路（Azure 上是 Bing）。搜尋另外計費；在 Azure 上，搜尋內容會離開 Azure 的資料邊界。");
    public static string SearchingWeb => T("Searching the web…", "正在搜尋網路…");
    public static string Sources => T("Sources", "來源");
    public static string QualityTooltip(string effort, string image) =>
        T($"Response quality\nReasoning: {effort}\nImages: {image}", $"回應品質\n推理：{effort}\n生圖：{image}");
    public static string OpenFile => T("Open file", "開啟檔案");
    public static string GeneratedImage => T("Generated image", "生成的圖片");

    // Artifacts
    public static string WritingArtifact(string title) => T($"Writing {title}…", $"正在撰寫「{title}」…");
    public static string ArtifactWriting => T("Writing…", "撰寫中…");
    public static string ArtifactClickToOpen => T("Click to open", "點擊開啟");
    public static string ArtifactDiagram => T("Diagram", "圖表");
    public static string ArtifactDocument => T("Document", "文件");
    public static string ArtifactPreview => T("Preview", "預覽");
    public static string ArtifactCode => T("Code", "程式碼");
    public static string ArtifactOpenInBrowser => T("Open in browser", "在瀏覽器開啟");
    public static string ArtifactSaveAs => T("Save as…", "另存新檔…");
    public static string ArtifactClose => T("Close panel", "關閉面板");
    public static string ArtifactVersion(int version) => T($"Version {version}", $"第 {version} 版");
    public static string ReplyError(string message) => T($"**Error while generating a reply:** {message}", $"**產生回覆時發生錯誤：** {message}");
    public static string NotConfigured => T(
        "Harness.WinUI isn't connected to a model provider yet. Use the settings (gear) button to add an endpoint, model, and API key.",
        "Harness.WinUI 還沒有連上模型服務。請按設定（齒輪）按鈕填入 endpoint、模型與 API key。");

    // Reply footer
    public static string TokensInOut(long input, long output) => T($"{input:N0} in / {output:N0} out tokens", $"輸入 {input:N0} / 輸出 {output:N0} tokens");
    public static string MetaModel(string model) => T($"Model: {model}", $"模型：{model}");
    public static string MetaInput(long input, long? cached) => T($"Input tokens: {input:N0}", $"輸入 tokens：{input:N0}") + (cached > 0 ? T($" ({cached:N0} cached)", $"（快取 {cached:N0}）") : "");
    public static string MetaOutput(long output, long? reasoning) => T($"Output tokens: {output:N0}", $"輸出 tokens：{output:N0}") + (reasoning > 0 ? T($" ({reasoning:N0} reasoning)", $"（思考 {reasoning:N0}）") : "");
    public static string MetaFirstToken(double seconds) => T($"First token: {seconds:F2}s", $"首個 token：{seconds:F2} 秒");
    public static string MetaTotal(double seconds) => T($"Total: {seconds:F2}s", $"總時間：{seconds:F2} 秒");

    // Tools button tooltip
    public static string BuiltInTools(int count) => T($"Built-in: {count} tools", $"內建：{count} 個工具");
    public static string FileToolsIn(string folder) => T($"Files: {folder}", $"檔案：{folder}");
    public static string FileToolsOff => T("File tools: off (Settings → Sandbox)", "檔案工具：關閉（設定 → 沙盒）");
    public static string NoMcpServers => T("No MCP servers — click to add one", "尚未設定 MCP 伺服器 — 點此新增");
    public static string ServerTools(string name, int count) => T($"{name}: {count} tools", $"{name}：{count} 個工具");
    public static string ServerStarting(string name) => T($"{name}: starting…", $"{name}：啟動中…");
    public static string ServerDisabled(string name) => T($"{name}: disabled", $"{name}：已停用");
    public static string ServerFailed(string name, string? error) => T($"{name}: failed — {error}", $"{name}：失敗 — {error}");

    // Startup / provider errors
    public static string ConfigLoadFailed(string message) => T($"Harness.WinUI could not load its configuration: {message}", $"Harness.WinUI 無法載入設定：{message}");
    public static string ProviderConnectFailed(string model, string endpoint, string message) =>
        T($"Harness.WinUI could not connect to its model provider ({model} @ {endpoint}): {message}", $"Harness.WinUI 無法連線到模型服務（{model} @ {endpoint}）：{message}");
    public static string ProviderStartFailed(string message) => T($"Harness.WinUI failed to start the model provider: {message}", $"Harness.WinUI 無法啟動模型服務：{message}");
    public static string McpConfigReadFailed(string path, string message) => T($"Couldn't read MCP config ({path}): {message}", $"無法讀取 MCP 設定（{path}）：{message}");

    // Settings dialog
    public static string SettingsTitle => T("Settings", "設定");
    public static string Save => T("Save", "儲存");
    public static string Cancel => T("Cancel", "取消");
    public static string LanguageHeader => T("Language", "語言");
    public static string LanguageSystem => T("System default", "跟隨系統");
    public static string LanguageRestartNote => T("Language changes apply after restarting Harness.WinUI.", "變更語言後需重新啟動 Harness.WinUI 才會套用。");
    public static string ProviderHeader => T("Model provider", "模型服務");
    public static string ProviderIntro => T(
        "An OpenAI-compatible endpoint, model and API key. The API key is encrypted on this device and never stored in plain text.",
        "OpenAI 相容的 endpoint、模型與 API key。API key 會在這台電腦上加密儲存，不會以明碼存放。");
    public static string Endpoint => "Endpoint";
    public static string Model => T("Model", "模型");
    public static string ImageModel => T("Image model (optional)", "生圖模型（選填）");
    public static string ImageModelPlaceholder => T("gpt-image-2 — leave blank to disable image generation", "gpt-image-2 — 留白則停用生圖");
    public static string ApiKey => "API key";
    public static string ApiKeyEnter => T("Enter API key", "輸入 API key");
    public static string ApiKeyKeep => T("Leave blank to keep the current key", "留白則沿用目前的 key");
    public static string ApiType => "API";
    public static string ApiAuto => T("Auto (recommended)", "自動（建議）");
    public static string ApiHelp => T(
        "Auto uses Responses for Azure OpenAI and OpenAI (needed for reasoning models to use tools) and Chat Completions for other gateways such as LiteLLM.",
        "自動：Azure OpenAI 與 OpenAI 使用 Responses（推理模型要搭配工具時必須），其他 gateway（例如 LiteLLM）使用 Chat Completions。");
    public static string SandboxHeader => T("Sandbox folder", "沙盒資料夾");
    public static string SandboxHelp => T(
        "Harness.WinUI's file tools can only read and write inside this folder (Word, Excel, PowerPoint and PDF files can be read too). Leave empty to turn file tools off.",
        "Harness.WinUI 的檔案工具只能在這個資料夾內讀寫（也能讀 Word、Excel、PowerPoint、PDF）。留白則關閉檔案工具。");
    public static string SandboxNone => T("Not set — file tools are off", "未設定 — 檔案工具未啟用");
    public static string Browse => T("Browse…", "瀏覽…");
    public static string EndpointModelRequired => T("Endpoint and Model are required.", "Endpoint 與模型為必填。");
    public static string ApiKeyRequired => T("An API key is required.", "請輸入 API key。");
    public static string SaveFailed(string message) => T($"Couldn't save settings: {message}", $"無法儲存設定：{message}");
    public static string SandboxFailed(string message) => T($"Couldn't set the sandbox folder: {message}", $"無法設定沙盒資料夾：{message}");

    // MCP dialog
    public static string McpTitle => T("MCP servers", "MCP 伺服器");
    public static string Done => T("Done", "完成");
    public static string McpIntro => T(
        "Tools from connected servers are available to Harness.WinUI. Changes apply immediately — no restart needed.",
        "已連線伺服器的工具都可以給 Harness.WinUI 使用。變更會立即生效，不需要重新啟動。");
    public static string McpEmpty => T(
        "No MCP servers yet. Add a remote one below, or ask Harness.WinUI to add one for you.",
        "還沒有 MCP 伺服器。可以在下方新增遠端伺服器，或直接請 Harness.WinUI 幫你新增。");
    public static string McpServerList => T("Configured MCP servers", "已設定的 MCP 伺服器");
    public static string Enabled => T("Enabled", "啟用");
    public static string Remove => T("Remove", "移除");
    public static string RemoveServer => T("Remove server", "移除伺服器");
    public static string RemoveConfirm(string name) => T($"Remove '{name}'?", $"要移除「{name}」嗎？");
    public static string Removed(string name) => T($"Removed '{name}'.", $"已移除「{name}」。");
    public static string AddRemoteServer => T("Add a remote server (Streamable HTTP)", "新增遠端伺服器（Streamable HTTP）");
    public static string Name => T("Name", "名稱");
    public static string Url => "URL";
    public static string AuthHeader => T("Auth header (optional)", "驗證 header（選填）");
    public static string HeaderValue => T("Value", "值");
    public static string AddAndConnect => T("Add and connect", "新增並連線");
    public static string HeaderEncryptedNote => T(
        "Header values are encrypted on this device; mcp.json only stores a reference to them.",
        "Header 的值會在這台電腦上加密儲存，mcp.json 只保存參照。");
    public static string EditMcpJson => T("Edit mcp.json (advanced)…", "編輯 mcp.json（進階）…");
    public static string ReloadFromFile => T("Reload from file", "從檔案重新載入");
    public static string StatusConnected(int tools) => T($"Connected · {tools} tools", $"已連線 · {tools} 個工具");
    public static string StatusConnecting => T("Connecting…", "連線中…");
    public static string StatusDisabled => T("Disabled", "已停用");
    public static string StatusFailed(string? error) => T($"Failed: {error}", $"失敗：{error}");
    public static string NameUrlRequired => T("Name and URL are required.", "名稱與 URL 為必填。");
    public static string ServerExists(string name) => T($"A server named '{name}' already exists. Remove it first or pick another name.", $"已經有名為「{name}」的伺服器，請先移除或換個名稱。");
    public static string ConnectingTo(string name) => T($"Connecting to {name}…", $"正在連線到 {name}…");
    public static string ConnectedWith(string name, int tools) => T($"Connected '{name}' with {tools} tools.", $"已連線「{name}」，共 {tools} 個工具。");
    public static string SavedButFailed(string name, string? error) => T($"Saved '{name}', but it couldn't connect: {error}", $"已儲存「{name}」，但無法連線：{error}");
    public static string AddFailed(string message) => T($"Couldn't add the server: {message}", $"無法新增伺服器：{message}");
    public static string AfterEditingReload => T("After editing, click \"Reload from file\".", "編輯完成後，請按「從檔案重新載入」。");
    public static string OpenMcpJsonFailed(string message) => T($"Couldn't open mcp.json: {message}", $"無法開啟 mcp.json：{message}");
    public static string Reloading => T("Reloading…", "重新載入中…");
    public static string Reloaded => T("Reloaded from mcp.json.", "已從 mcp.json 重新載入。");

    // Usage and cost
    public static string TokensShort(string tokens) => T($"{tokens} tokens", $"{tokens} tokens");
    public static string CostTooltip(string cost, decimal input, decimal cached, decimal output, string currency) =>
        T($"Cost: {cost} (per 1M tokens: input {input}, cached input {cached}, output {output} {currency})",
          $"費用：{cost}（每 100 萬 token：輸入 {input}、快取輸入 {cached}、輸出 {output} {currency}）");
    public static string NoPriceTooltip(string model) =>
        T($"No price set for {model}: add one in Settings to see costs.", $"尚未設定 {model} 的價格：在設定中填入後即可顯示費用。");
    public static string UsageTooltip => T("Usage and cost", "用量與費用");
    public static string UsageThisConversation => T("This conversation", "本次對話");
    public static string UsageToday => T("Today", "今天");
    public static string UsageThisMonth => T("This month", "本月");
    public static string UsageAllTime => T("All time", "全部");
    public static string UsageReplies(int n) => T($"{n} replies", $"{n} 次回覆");
    public static string UsageTokens(string input, string output) => T($"in {input} · out {output}", $"輸入 {input} · 輸出 {output}");
    public static string UsageUnpriced(int n) => T($"{n} without a price", $"{n} 次未設定價格");
    public static string UsageNone => T("No usage yet.", "還沒有用量紀錄。");
    public static string UsageUnavailable => T("Usage tracking is unavailable.", "無法記錄用量。");
    public static string PricingHeader => T("Pricing", "費用");
    public static string PricingNote => T("Prices per 1M tokens for the model above. Leave empty to show tokens only.",
                                          "上方模型每 100 萬 token 的價格。留空則只顯示 token 數。");
    public static string Currency => T("Currency", "幣別");
    public static string PriceInput => T("Input", "輸入");
    public static string PriceCachedInput => T("Cached input", "快取輸入");
    public static string PriceOutput => T("Output", "輸出");
    public static string CurrencyInvalid => T("Currency must be a 3-letter code such as USD or TWD.", "幣別必須是 3 個英文字母的代碼，例如 USD 或 TWD。");

    // Skills
    public static string SkillsHeader => T("Skills", "技能（Skills）");
    public static string SkillsIntro => T(
        "Folders with a SKILL.md that teach the agent a kind of task. Found in Harness.WinUI's skills folder, ~/.agents/skills, ~/.claude/skills and the sandbox's .agents/skills; the agent loads one when a request matches it.",
        "資料夾內放一份 SKILL.md，教 agent 做某一類工作。會從 Harness.WinUI 的技能資料夾、~/.agents/skills、~/.claude/skills 與沙盒的 .agents/skills 讀取；請求符合時 agent 會自動載入。");
    public static string SkillsEmpty => T("No skills yet. Open the skills folder and add one (a folder with a SKILL.md).", "還沒有技能。開啟技能資料夾，新增一個含 SKILL.md 的資料夾即可。");
    public static string SkillProblem(string path, string message) => T($"Skipped {path}: {message}", $"略過 {path}：{message}");
    public static string OpenSkillsFolder => T("Open skills folder", "開啟技能資料夾");
    public static string Refresh => T("Refresh", "重新整理");

    // Notifications (when the window is in the background)
    public static string NotifyReplyDone => T("Reply finished", "已完成回覆");
    public static string NotifyReplyFailed => T("The reply failed", "回覆時發生錯誤");
    public static string NotifyApprovalTitle => T("Waiting for your approval", "等待你的核准");
    public static string NotifyApprovalBody(string tool) => T($"Harness.WinUI wants to run {tool}.", $"Harness.WinUI 想要執行 {tool}。");

    // Observability
    public static string ObservabilityTitle => T("Observability", "觀測");
    public static string ObservabilityTooltip => T("Observability: what is sent to the model and what comes back", "觀測：送給模型的內容與回應");
    public static string TraceRecording => T("Recording", "記錄中");
    public static string TraceNotRecording => T("Not recording", "未記錄");
    public static string TraceRange24Hours => T("Last 24 hours", "過去 24 小時");
    public static string TraceRange7Days => T("Last 7 days", "過去 7 天");
    public static string TraceRange30Days => T("Last 30 days", "過去 30 天");
    public static string TraceSearch => T("Search messages, tools or models", "搜尋訊息、工具或模型");
    public static string TraceFailedOnly => T("Errors only", "只看錯誤");
    public static string TraceSettings => T("Recording settings", "記錄設定");
    public static string TraceCaptureContent => T("Record message content (prompts, replies, tool arguments and results)", "記錄訊息內容（提示、回覆、工具參數與結果）");
    public static string TraceRetention => T("Keep records for (days)", "紀錄保留天數");
    public static string TraceClear => T("Delete all records", "刪除所有紀錄");
    public static string TraceClearConfirm => T("Delete every recorded turn? This can't be undone.", "要刪除所有紀錄嗎？刪除後無法復原。");
    public static string Delete => T("Delete", "刪除");
    public static string TraceOffTitle => T("Recording is off", "觀測記錄未開啟");
    public static string TraceOffBody(int days) => T(
        $"Turn on Recording to keep what Harness.WinUI sends to the model, what comes back, and every tool call. Records stay on this PC and are deleted after {days} days.",
        $"開啟「記錄」後，會保存 Harness.WinUI 送給模型的內容、模型的回應，以及每一次工具呼叫。紀錄只存在這台電腦，{days} 天後自動刪除。");
    public static string TraceUnavailable => T("The observability database couldn't be opened.", "無法開啟觀測資料庫。");
    public static string TraceEmpty => T("No recorded turns in this period.", "這段期間沒有紀錄。");
    public static string TraceSelectTurn => T("Select a turn on the left to see its model and tool calls.", "選擇左側的一筆紀錄，查看模型與工具呼叫。");
    public static string TraceModelCalls(int n) => T($"{n} model calls", $"{n} 次模型呼叫");
    public static string TraceToolCalls(int n) => T($"{n} tool calls", $"{n} 次工具呼叫");
    public static string TraceTabContent => T("Content", "內容");
    public static string TraceTabRequest => T("Raw request", "原始請求");
    public static string TraceTabResponse => T("Raw response", "原始回應");
    public static string TraceTabDetails => T("Details", "屬性");
    public static string TraceSystem => T("System instructions", "系統指示");
    public static string TraceToolsOffered(int n) => T($"Tools offered ({n})", $"提供給模型的工具（{n}）");
    public static string TraceInput(int n) => T($"Input ({n} messages)", $"輸入（{n} 則訊息）");
    public static string TraceOutput => T("Output", "輸出");
    public static string TraceArguments => T("Arguments", "參數");
    public static string TraceResult => T("Result", "結果");
    public static string TraceUserMessage => T("User message", "使用者訊息");
    public static string TraceReply => T("Reply", "回覆");
    public static string TraceNoContent => T("Content wasn't recorded (Recording settings).", "未記錄內容（見記錄設定）。");
    public static string TraceNoRaw => T("No raw HTTP exchange was recorded for this step.", "這一步沒有原始 HTTP 紀錄。");
    public static string TraceTurn => T("Turn", "回合");
    public static string TraceModelCall(string? model) => T($"Model call · {model}", $"模型呼叫 · {model}");
    public static string TraceToolCall(string? name) => T($"Tool · {name}", $"工具 · {name}");
    public static string TraceApproval(string? decision) => T($"Approval · {decision}", $"核准 · {decision}");
    public static string TraceDecision(string? decision) => decision switch
    {
        "AllowOnce" => T("allowed once", "允許一次"),
        "AlwaysAllow" => T("always allowed", "永遠允許"),
        "Deny" => T("denied", "拒絕"),
        _ => T("no answer", "未回應"),
    };
    public static string TraceFailed => T("Failed", "失敗");
    public static string TraceTokens(string tokens) => T($"{tokens} tok", $"{tokens} tok");
    public static string TraceViewLog => T("Log", "紀錄");
    public static string TraceViewStats => T("Statistics", "統計");
    public static string StatTurns => T("Turns", "回合");
    public static string StatTurnsTip => T(
        "One turn: you send a message and the agent works until it has replied, including every model and tool call in between.",
        "一個回合：你送出一則訊息，到 agent 回覆完成為止（中間所有的模型呼叫與工具呼叫都算在內）。");
    public static string StatFailed => T("Failed", "失敗");
    public static string StatFailedTip => T("Turns that ended with an error. Turns you stopped yourself aren't counted.", "以錯誤結束的回合。你自己按停止的不算。");
    public static string StatModelCalls => T("Model calls", "模型呼叫");
    public static string StatModelCallsTip => T(
        "Requests sent to the model. One turn usually makes several: each time the agent uses a tool, the model is called again with its result.",
        "送給模型的請求次數。一個回合通常會有好幾次：agent 每用一次工具，就會帶著結果再呼叫模型一次。");
    public static string StatToolCalls => T("Tool calls", "工具呼叫");
    public static string StatToolCallsTip => T("How many times the agent ran a tool (built-in, skill or MCP).", "agent 實際執行工具的次數（內建、技能或 MCP 都算）。");
    public static string StatTokens => T("Tokens in / out", "Token 輸入 / 輸出");
    public static string StatTokensTip => T(
        "Input: everything sent to the model (instructions, tool definitions, conversation so far). Output: what the model wrote back.",
        "輸入：送給模型的全部內容（指示、工具說明、到目前為止的對話）。輸出：模型回寫的內容。");
    public static string StatCacheHit => T("Cache hit rate", "快取命中率");
    public static string StatCacheHitTip => T(
        "The share of input tokens the provider read from its prompt cache instead of processing again. Higher is cheaper and faster. \"—\" means the provider doesn't report it.",
        "輸入 token 之中，有多少比例是模型供應商直接從提示快取讀取、不必重新處理。越高越省錢也越快。顯示「—」代表供應商沒有回報。");
    public static string StatCost => T("Cost", "費用");
    public static string StatCostTip => T("Estimated from the prices you set in Settings.", "依你在設定中填的單價估算。");
    public static string StatAverageTurn => T("Average turn", "平均回合時間");
    public static string StatAverageTurnTip => T("Average time from sending a message to the finished reply.", "從送出訊息到回覆完成的平均時間。");
    public static string StatModels => T("Models", "模型");
    public static string StatModelsNote => T(
        "Time is per model call. Median: half the calls were faster. P95: only 5% were slower. First token: how long until the reply starts to appear. Cache hit: the share of input read from the prompt cache (higher is cheaper). Tool definitions: size of the tool descriptions sent with every call.",
        "耗時以單次模型呼叫計算。中位數：一半的呼叫比它快。P95：只有 5% 的呼叫比它慢。首字：送出後多久開始出現回覆。快取命中：輸入中由提示快取讀取的比例（越高越省）。工具定義大小：每次呼叫都會附上的工具說明長度。");
    public static string StatTools => T("Tools", "工具");
    public static string StatToolsNote => T(
        "Offered: how many model calls had this tool attached so the model could use it. Called / offered: how often it was actually used. Every attached tool adds its description to the input tokens, so greyed-out tools (offered but never called) are candidates to turn off.",
        "提供：這個工具被附在幾次模型呼叫裡，讓模型可以使用。呼叫率：實際被用到的比例。每個被附上的工具都會把它的說明加進輸入 token，所以灰色的工具（常被提供、從未被呼叫）可以考慮關閉。");
    public static string StatSkills => T("Skills", "技能");
    public static string StatSkillsNote => T(
        "Loads: how many times the agent loaded the skill. Files read: skill files it opened after loading.",
        "載入：agent 載入這個技能的次數。讀取檔案：載入後又打開技能資料夾裡檔案的次數。");
    public static string StatMcpServers => T("MCP servers", "MCP 伺服器");
    public static string StatMcpServersNote => T("The calls to each MCP server's tools, added up.", "各個 MCP 伺服器的工具被呼叫的合計。");
    public static string StatApprovals => T("Approvals", "核准");
    public static string StatApprovalsNote => T(
        "Times the agent asked before running a tool, and what you answered. Average wait: how long it waited for you; the turn is paused meanwhile.",
        "agent 執行工具前詢問你的次數，以及你的回答。平均等待：它等你回答的時間，這段時間整個回合都是暫停的。");
    public static string StatNone => T("Nothing recorded in this period.", "這段期間沒有資料。");
    public static string ColModel => T("Model", "模型");
    public static string ColCalls => T("Calls", "呼叫");
    public static string ColFailures => T("Failed", "失敗");
    public static string ColP50 => T("Median time", "耗時中位數");
    public static string ColP95 => T("P95 time", "耗時 P95");
    public static string ColTtft => T("First token", "首字");
    public static string ColInput => T("Input", "輸入");
    public static string ColCacheHit => T("Cache hit", "快取命中");
    public static string ColOutput => T("Output", "輸出");
    public static string ColCost => T("Cost", "費用");
    public static string ColToolDefs => T("Tool definitions", "工具定義大小");
    public static string ColTool => T("Tool", "工具");
    public static string ColSource => T("Source", "來源");
    public static string ColOffered => T("Offered", "提供");
    public static string ColUseRate => T("Called / offered", "呼叫率");
    public static string ColAverage => T("Average", "平均耗時");
    public static string ColMax => T("Slowest", "最慢");
    public static string ColResultSize => T("Result size", "結果大小");
    public static string ColSkill => T("Skill", "技能");
    public static string ColLoads => T("Loads", "載入");
    public static string ColFileReads => T("Files read", "讀取檔案");
    public static string ColLastUsed => T("Last used", "最後使用");
    public static string ColServer => T("Server", "伺服器");
    public static string ColDecision => T("Decision", "決定");
    public static string ColCount => T("Count", "次數");
    public static string ColWait => T("Average wait", "平均等待");
    public static string AnalysisTitle => T("AI analysis", "AI 分析");
    public static string AnalysisNote => T(
        "Hand this period's statistics and records to your model and let it explain them and suggest improvements. Click \"Analyze this period\" for an overview, or ask your own question. The data is sent to the model you set up in Settings, including message content if you record it. The analysis itself isn't recorded.",
        "把這段期間的統計與紀錄交給你設定的模型，由它解讀並提出改善建議。按「分析這段期間」看整體分析，也可以自己提問。資料會送到你在設定中選的模型；如果你有記錄訊息內容，訊息內容也會一併送出。分析本身不會被記錄。");
    public static string AnalysisPlaceholder => T("e.g. Which step is slowest? Which tools could I turn off? How can I raise the cache hit rate?", "例如：哪個步驟最慢？哪些工具可以關掉？怎樣提高快取命中率？");
    public static string AnalysisAsk => T("Ask", "提問");
    public static string AnalysisStop => T("Stop", "停止");
    public static string AnalysisOverview => T("Analyze this period", "分析這段期間");
    public static string AnalysisOverviewQuestion => T(
        "Analyze this period: where time goes (slow model calls, tools, approval waits), failures and their causes, cost, prompt-cache hit rate, and how tools and skills are used. Give concrete suggestions.",
        "請分析這段期間：時間花在哪裡（慢的模型呼叫、工具、等待核准）、失敗與原因、成本、快取命中率，以及工具與技能的使用情況，並給出具體建議。");
    public static string AnalysisNoModel => T("No model is configured. Set one up in Settings first.", "尚未設定模型，請先到設定中填入。");
    public static string AnalysisFailed(string message) => T($"The analysis failed: {message}", $"分析失敗：{message}");
    public static string ExportSettings => T("OTLP export…", "OTLP 匯出…");
    public static string ExportTitle => T("OTLP export", "OTLP 匯出");
    public static string ExportIntro => T(
        "Also send the records to an OpenTelemetry backend you run or use, such as Aspire Dashboard, Langfuse or an OpenTelemetry Collector. Off unless you set it up here.",
        "把紀錄另外送到你自己架設或使用的 OpenTelemetry 後台，例如 Aspire Dashboard、Langfuse 或 OpenTelemetry Collector。沒在這裡設定就不會送出。");
    public static string ExportEnabled => T("Export", "匯出");
    public static string ExportEndpoint => T("Endpoint", "端點");
    public static string ExportEndpointHelp => T(
        "HTTP: the base URL (e.g. http://localhost:4318, or Langfuse's …/api/public/otel); /v1/traces is added. gRPC: the address, e.g. http://localhost:4317.",
        "HTTP：填基本網址（例如 http://localhost:4318，或 Langfuse 的 …/api/public/otel），會自動加上 /v1/traces。gRPC：填位址，例如 http://localhost:4317。");
    public static string ExportProtocol => T("Protocol", "協定");
    public static string ExportHeaders => T("Headers", "Headers");
    public static string ExportHeadersHelp => T(
        "name=value pairs separated by commas, e.g. Authorization=Basic … for Langfuse. Saved encrypted for your Windows account.",
        "以逗號分隔的 name=value，例如 Langfuse 的 Authorization=Basic …。會加密保存，只有你的 Windows 帳號能解開。");
    public static string ExportHeadersSaved => T("Saved — leave empty to keep", "已儲存（留空則保留）");
    public static string ExportHeadersRemove => T("Remove saved headers", "移除已儲存的 headers");
    public static string ExportMetrics => T("Also export metrics (token usage, durations) — not every backend accepts them", "同時匯出指標（token 用量、耗時）；不是每個後台都支援");
    public static string ExportContentNote => T(
        "What is exported follows the recording settings: message content only when \"Record message content\" is on. Raw HTTP exchanges stay on this PC.",
        "匯出的內容與記錄設定相同：只有開啟「記錄訊息內容」時才會包含訊息內容。原始 HTTP 內容只留在這台電腦。");
    public static string ExportEndpointInvalid => T("Enter a full URL, e.g. http://localhost:4318.", "請輸入完整網址，例如 http://localhost:4318。");
    public static string ExportFailed(string message) => T($"Couldn't start the export: {message}", $"無法開始匯出：{message}");
    public static string Chars(string n) => T($"{n} chars", $"{n} 字元");
    public static string TraceTtft(string seconds) => T($"first token {seconds}", $"首字 {seconds}");

    // Toolbar
    public static string MoreTooltip => T("More", "更多");
    public static string CustomizeToolbar => T("Customize toolbar", "自訂工具列");
    public static string CustomizeToolbarEllipsis => T("Customize toolbar…", "自訂工具列…");
    public static string Apply => T("Apply", "套用");
    public static string ShowOnToolbar => T("Show on toolbar", "顯示在工具列上");

    // Attachments (paste / drag and drop)
    public static string Attachments => T("Attachments", "附件");
    public static string RemoveAttachment => T("Remove attachment", "移除附件");
    public static string DropHint => T("Drop to attach", "放開以附加檔案");
    public static string AttachCaption => T("Attach", "附加");
    public static string TooManyAttachments => T($"Up to {ViewModels.ChatViewModel.MaxAttachments} attachments per message.", $"每則訊息最多 {ViewModels.ChatViewModel.MaxAttachments} 個附件。");
    public static string AttachmentTooLarge(string name) => T($"{name} is too large to attach.", $"{name} 太大，無法附加。");
    public static string AttachmentFailed(string name, string message) => T($"Couldn't attach {name}: {message}", $"無法附加 {name}：{message}");
    public static string AttachmentUnsupported(string name) => T($"{name} isn't a text, image, Office or PDF file.", $"{name} 不是文字、圖片、Office 或 PDF 檔。");
    public static string AttachmentUnreadable(string name) => T($"Couldn't read {name}.", $"無法讀取 {name}。");

    // MCP Apps
    public static string AppToolApprovalTitle(string server) => T($"An app from {server} wants to run a tool", $"{server} 的 App 想要執行工具");
    public static string AppToolApprovalBody(string tool) => T($"Allow \"{tool}\" to run with these arguments?", $"要允許執行「{tool}」嗎？參數如下：");
    public static string AppUnavailable(string tool, string message) => T($"Couldn't show the app for {tool}: {message}", $"無法顯示 {tool} 的 App：{message}");
}
