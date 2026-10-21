// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>Texts for working on code: how much of a file was read, workspaces, tasks, helpers, YOLO mode.</summary>
public static partial class Strings
{
    // Chat / Code mode and the project folder
    public static string ModeSelectorName => T("Mode", "模式");
    public static string ModeChat => T("Chat", "對話");
    public static string ModeCode => T("Code", "程式");
    public static string ModeBusy => T("Wait for the reply to finish first", "請等這則回覆結束再切換");
    public static string WorkspaceButtonName => T("Project folder", "專案資料夾");
    public static string WorkspaceNone => T("Choose a project folder", "選擇專案資料夾");
    public static string WorkspacePick => T("Choose another folder…", "選擇其他資料夾…");
    public static string WorkspaceReveal => T("Show in File Explorer", "在檔案總管中顯示");
    public static string InFolder(string folder) => T($"in {folder}", $"只在 {folder}");

    public static string TasksHeader(int done, int total) => T($"Tasks {done}/{total}", $"任務 {done}/{total}");

    public static string ReadOnlyBadge => T("Read-only", "唯讀");
    public static string ReadOnlyBadgeTooltip => T("Read-only mode is on: the agent can't change anything. Click to turn it off.", "唯讀模式已開啟：agent 不能改任何東西。按一下關閉。");
    public static string YoloBadgeTooltip => T("YOLO mode is on: nothing the agent does is asked about. Click to turn it off.", "全自動模式（YOLO）已開啟：agent 做的事都不會再詢問。按一下關閉。");

    // Helper agents (spawn_agent): the step that shows what each one is doing
    public static string HelperRunning(int steps, TimeSpan elapsed) => T($"working · {StepCount(steps)} · {Elapsed(elapsed)}", $"執行中 · {StepCount(steps)} · {Elapsed(elapsed)}");
    public static string HelperSummary(int steps, TimeSpan elapsed) => T($"{StepCount(steps)} · {Elapsed(elapsed)}", $"{StepCount(steps)} · {Elapsed(elapsed)}");
    public static string HelperPanelHeader(int running, int total) => running > 0
        ? T($"Helpers · {running} working", $"小幫手 · {running} 個執行中")
        : T($"Helpers · {total} done", $"小幫手 · 已完成 {total} 個");
    public static string HelperStopTooltip => T("Stop this helper (it reports what it found so far)", "停止這個小幫手（它會回報目前找到的）");
    public static string ExploreDescription => T(
        "Look into a question with 2 to 4 helpers working at the same time, then answer from their reports. Without a question: an overview of the project.",
        "用 2～4 個小幫手同時調查一個問題，再整理出答案。不寫問題就是這個專案的概覽。");
    public static string ExploreOverview => "Give me an overview of this project: what it is for, how it is built (main parts and how they fit together), where the main flows are, and how to build and run it.";    /// <summary>Why a helper stopped early, as the helper tool words it for the model, in the person's language.</summary>
    public static string HelperWhy(string why) => why.Length == 0 ? why
        : why.Contains("by you", StringComparison.Ordinal) ? T("(stopped by you)", "（已由你停止）")
        : T("(stopped early)", "（提前中止）");
    public static string HelperQuestion => T("Question", "問題");
    public static string HelperEarlier(int count) => T($"… {count} earlier step{(count == 1 ? "" : "s")}", $"… 前面還有 {count} 步");
    private static string StepCount(int steps) => T($"{steps} step{(steps == 1 ? "" : "s")}", $"{steps} 步");
    private static string Elapsed(TimeSpan time) => time.TotalSeconds < 60
        ? T($"{time.TotalSeconds:0} s", $"{time.TotalSeconds:0} 秒")
        : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    public static string CodeMcpServers => T("MCP servers used in Code mode", "程式模式使用的 MCP 伺服器");

    public static string InitDescription => T(
        "Write AGENTS.md for this project: how to build and test it, its layout and conventions (read at the start of every conversation).",
        "替這個專案寫 AGENTS.md：怎麼建置與測試、結構與慣例（每次對話開頭都會讀）。");

    // Undo and diffs
    public static string UndoChip(int files) => T($"Undo changes ({files} file{(files == 1 ? "" : "s")})", $"還原這一輪的修改（{files} 個檔案）");
    public static string UndoChipTooltip => T(
        "Put the files this reply changed back as they were. Changes made by commands the agent ran are not covered.",
        "把這一則回覆改過的檔案還原成改之前的樣子。agent 執行指令造成的變更不在範圍內。");
    public static string UndoConfirmTitle => T("Undo this reply's changes?", "還原這一則回覆的修改？");
    public static string UndoConfirmBody(string list) => T(
        $"These files go back to how they were before the reply (files it created are deleted). Anything you changed in them since is lost too.\n\n{list}",
        $"這些檔案會還原成這則回覆之前的樣子（它新建的檔案會被刪除）。之後你自己改過的內容也會一併被還原。\n\n{list}");
    public static string UndoConfirmButton => T("Undo changes", "還原");
    public static string UndoDone => T("Changes undone", "已還原");
    public static string UndoPartly(int problems) => T($"Undone, except {problems} file(s)", $"已還原，但有 {problems} 個檔案無法還原");
    public static string NewFile(int lines) => T($"(new file, {lines} line{(lines == 1 ? "" : "s")})", $"（新檔案，{lines} 行）");

    // YOLO mode
    public static string YoloMode => T("YOLO mode (don't ask)", "全自動模式（YOLO，不再詢問）");
    public static string YoloTooltipOff => T(
        "YOLO mode: the agent does everything without asking, commands included. Off now; click to turn on.",
        "全自動模式（YOLO）：agent 做任何事都不再詢問，包含執行指令。目前關閉，按一下開啟。");
    public static string YoloTooltipOn => T(
        "YOLO mode is ON: nothing the agent does is asked about. Click to turn off.",
        "全自動模式（YOLO）已開啟：agent 做的事都不會再詢問。按一下關閉。");
    public static string YoloTurnedOn => T("YOLO mode on", "全自動模式已開啟");
    public static string YoloTurnedOff => T("YOLO mode off", "全自動模式已關閉");
    public static string YoloConfirmTitle => T("Turn on YOLO mode?", "開啟全自動模式（YOLO）？");
    public static string YoloConfirmBody => T(
        "The agent will write files and run commands, including commands that chain or pipe, without asking you first. A page or file it reads could talk it into doing something you didn't want. Only changes to this app's own settings (such as adding an MCP server) are still asked about. Use it in folders you trust; it turns off by itself when you close the app.",
        "agent 會直接寫檔、執行指令（包含串接、管線的指令），不會先問你。它讀到的網頁或檔案，有可能誘使它做你不想要的事。只有更改這個 App 自己的設定（例如新增 MCP server）仍然會問。請只在你信任的資料夾使用；關閉 App 後會自動恢復成會詢問。");
    public static string YoloConfirmTurnOn => T("Turn on", "開啟");

    public static string ReadLines(string from, string to, string total) => T($"lines {from}-{to} of {total}", $"第 {from}–{to} 行（共 {total} 行）");
    public static string ReadWholeFile(string total) => T($"the whole file ({total} lines)", $"整個檔案（{total} 行）");
}
