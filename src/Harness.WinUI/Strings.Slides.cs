// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>Texts of the slide preview and the PowerPoint export.</summary>
public static partial class Strings
{
    public static string SlideWriting => T("Writing…", "撰寫中…");
    public static string SlideNotes => T("Notes", "備註");
    public static string SlidePresent => T("Present", "播放");
    public static string SlidePrevious => T("Previous slide", "上一頁");
    public static string SlideNext => T("Next slide", "下一頁");
    public static string SlideAdd => T("New slide after this one", "在這一頁後面新增一頁");
    public static string SlideDuplicate => T("Duplicate this slide", "複製這一頁");
    public static string SlideDelete => T("Delete this slide", "刪除這一頁");
    public static string SlideMoveEarlier => T("Move this slide earlier", "這一頁往前移");
    public static string SlideMoveLater => T("Move this slide later", "這一頁往後移");
    public static string SlideNewTitle => T("New slide", "新投影片");
    public static string SlideEditHint => T("Click any text to edit · Enter adds a line", "點一下文字即可編輯 · Enter 新增一行");
    public static string SlideNotesHint => T("Speaker notes", "講者備註");
    public static string SlideEmpty => T("The slides will show here as they are written.", "簡報寫好的頁面會依序顯示在這裡。");
    public static string ThemeMenu => T("Company theme", "公司主題");
    public static string ThemeAnalyze => T("Write the design rules with AI", "用 AI 寫這個主題的設計規則");
    public static string ThemeEditRules => T("Edit the design rules (Notepad)…", "編輯設計規則（記事本）…");
    public static string ThemeAnalyzing(string name) => T($"Reading the template \"{name}\"…", $"正在分析範本「{name}」…");
    public static string ThemeAnalyzed(string name) => T($"Design rules written for {name}", $"已為「{name}」寫好設計規則");
    public static string ThemeRulesKept(string name) => T($"Theme made: {name} (its earlier design rules are kept)", $"已建立主題：{name}（沿用先前的設計規則）");
    public static string ThemeAnalysisFailed(string reason) => T($"The analysis failed: {reason}", $"分析失敗：{reason}");
    public static string ThemeAnalysisNoModel => T("No model is set up for the analysis.", "還沒有設定可用的模型，無法分析。");
    public static string ThemeAnalysisEmpty => T("The model gave no usable guide; try again.", "模型沒有給出可用的規則，請再試一次。");
    public static string ThemeBuiltIn => T("Built-in (blue)", "內建（藍色）");
    public static string ThemeImport => T("Make a theme from a PowerPoint template…", "從 PowerPoint 範本建立主題…");
    public static string ThemeDelete => T("Delete a theme", "刪除主題");
    public static string ThemeImported(string name) => T($"Theme made: {name}", $"已建立主題：{name}");
    public static string ThemeImportFailed(string reason) => T($"Couldn't use this file: {reason}", $"這個檔案無法使用：{reason}");
    public static string ExportPowerPoint => T("Export as PowerPoint (.pptx)", "匯出成 PowerPoint（.pptx）");
    public static string ExportPowerPointFailed(string reason) => T($"Couldn't export: {reason}", $"無法匯出：{reason}");
    public static string ExportedPowerPoint => T("Exported", "已匯出");
}