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

    // Notifications (when the window is in the background)
    public static string NotifyReplyDone => T("Reply finished", "已完成回覆");
    public static string NotifyReplyFailed => T("The reply failed", "回覆時發生錯誤");
    public static string NotifyApprovalTitle => T("Waiting for your approval", "等待你的核准");
    public static string NotifyApprovalBody(string tool) => T($"Harness.WinUI wants to run {tool}.", $"Harness.WinUI 想要執行 {tool}。");

    // MCP Apps
    public static string AppToolApprovalTitle(string server) => T($"An app from {server} wants to run a tool", $"{server} 的 App 想要執行工具");
    public static string AppToolApprovalBody(string tool) => T($"Allow \"{tool}\" to run with these arguments?", $"要允許執行「{tool}」嗎？參數如下：");
    public static string AppUnavailable(string tool, string message) => T($"Couldn't show the app for {tool}: {message}", $"無法顯示 {tool} 的 App：{message}");
}
