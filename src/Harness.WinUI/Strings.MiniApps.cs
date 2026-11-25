// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>Texts of pinned apps: HTML artifacts pinned as apps in windows of their own.</summary>
public static partial class Strings
{
    public static string InstallAsApp => T("Pin as app", "釘選為 App");
    public static string MyApps => T("Pinned apps", "已釘選的 App");
    public static string NoAppsYet => T(
        "Nothing pinned yet. Open an HTML page the agent made and choose \"Pin as app\" in its panel.",
        "還沒有釘選。打開 agent 做的 HTML 頁面，在面板上選「釘選為 App」。");
    public static string Open => T("Open", "開啟");
    public static string Unpin => T("Unpin", "取消釘選");
    public static string AppUnpinConfirm => T("Sure? Data is deleted", "確定？資料會刪除");
    public static string AppPinned(string name) => T($"Pinned \"{name}\"", $"已釘選「{name}」");
    public static string AppPinnedOn(DateTime pinned, IReadOnlyList<string> permissions, bool approved) =>
        T($"Pinned {pinned:yyyy-MM-dd}", $"釘選於 {pinned:yyyy-MM-dd}") +
        (permissions.Count > 0 ? " · " + string.Join(", ", permissions) + (approved ? string.Empty : T(" (not allowed yet)", "（尚未允許）")) : string.Empty);
    public static string AppInstallFailed(string reason) => T($"Could not pin the app: {reason}", $"無法釘選 App：{reason}");

    // What an app asks for
    public static string AppAllowTitle => T("Allow this app?", "允許這個 App？");
    public static string AppWantsTo(string name) => T($"\"{name}\" asks to:", $"「{name}」想要：");
    public static string AppPermission(string permission) => permission switch
    {
        "llm" => T("Use your model (its calls count in your usage and cost)", "使用你設定的模型（用量與費用會計入你的帳）"),
        "storage" => T("Keep its own saved data", "保存它自己的資料"),
        "mcp" => T("Use your tools (not available yet)", "使用你的工具（尚未支援）"),
        _ => T($"{permission} (not available yet)", $"{permission}（尚未支援）"),
    };
    public static string AppAllowNote => T(
        "This is for this version of the app only. If its page changes, you are asked again. The page never sees your API key.",
        "只對這個版本有效；頁面一有變動就會再問你。頁面看不到你的 API 金鑰。");
    public static string Allow => T("Allow", "允許");
    public static string AppNotNow => T("Not now", "暫不允許");
}
