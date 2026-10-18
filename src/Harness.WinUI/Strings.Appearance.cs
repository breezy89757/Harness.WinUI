// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>Texts of the look-and-feel choices (conversation width) and the tips on an empty conversation.</summary>
public static partial class Strings
{
    // Settings → General
    public static string AppearanceHeader => T("Appearance", "外觀");
    public static string WidthHeader => T("Conversation width", "對話欄寬度");
    public static string WidthNarrow => T("Narrow", "窄");
    public static string WidthStandard => T("Standard", "標準");
    public static string WidthWide => T("Wide", "寬");
    public static string WidthFull => T("Full window", "整個視窗");
    public static string WidthHelp => T(
        "The widest the conversation gets in a big window; a small window is always filled. You can also just ask the agent, for example \"make the conversation wider\".",
        "視窗很大時，對話欄最寬到多寬；小視窗一律填滿。你也可以直接對 agent 說，例如「把對話欄放寬一點」。");
    public static string TipsHeader => T("Tips", "小提示");
    public static string TipsOn => T("Show a tip on an empty conversation", "在空白的新對話裡顯示小提示");
    public static string TipsOff => T("Don't show tips", "不顯示小提示");

    // The tips themselves
    public static string TipLabel => T("Tip", "小提示");
    public static string TipDismiss => T("Close this tip", "關閉這則小提示");
    public static string TipsTurnOff => T("Don't show tips again", "不再顯示小提示");
    public static string TipAppearance => T(
        "Want it to look different? Just say so, for example \"make the conversation wider\" or \"bigger text\". I'll ask you before changing anything.",
        "想要不同的版面？直接說就好，例如「把對話欄放寬一點」或「字大一點」。改之前我會先問你。");
    public static string TipToolbar => T(
        "Right-click the buttons at the bottom to choose which ones show. The others stay in the More menu.",
        "在底下的工具列按右鍵，可以選擇要顯示哪些按鈕。其他的會收在「⋯」選單裡。");
    public static string TipMemory => T(
        "Want me to remember how you like things done? Turn on memory in Settings → Memory. I ask before saving each note.",
        "想讓我記住你做事的習慣？到「設定 → 記憶」開啟。每一則存之前都會先問你。");
    public static string TipHistory => T(
        "Looking for where you talked about something before? Turn on searching past conversations in Settings → General.",
        "想找之前在哪個對話聊過某件事？到「設定 → 一般」開啟搜尋過去的對話。");
}
