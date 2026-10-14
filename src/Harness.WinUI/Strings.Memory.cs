// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI;

/// <summary>Texts of the long-term memory: the Settings page, the reply footer and the memory window.</summary>
public static partial class Strings
{
    // Settings
    public static string SettingsTabMemory => T("Memory", "記憶");
    public static string MemoryHeader => T("Long-term memory", "長期記憶");
    public static string MemoryOn => T("On", "開啟");
    public static string MemoryOff => T("Off", "關閉");
    public static string MemoryHelp => T(
        "The agent can remember short notes about you and your work, so you don't have to say them again in the next conversation. It asks you before it saves anything, and you can read, edit and delete every memory in the Memory window. Turning this off doesn't delete what is already saved; it just stops being used.",
        "agent 可以記住關於你和你工作的短短幾句話，下次對話你就不用再講一遍。存之前一定會先問你，你也能在「記憶」視窗讀、改、刪每一則。把這個關掉不會刪掉已經存的記憶，只是不再使用。");
    public static string MemoryBudget => T("Memories given to the model at the start of a conversation (tokens)", "每次對話開始時交給模型的記憶份量（token）");
    public static string MemoryBudgetHelp => T(
        "0 means none are given in advance; the agent can still look them up itself. They are chosen once per conversation, so they don't break the prompt cache.",
        "0 表示不事先給，agent 仍然可以自己去查。每個對話只挑一次，不會破壞提示快取。");
    public static string MemoryDefaultScope => T("Where a new memory is used by default", "新的記憶預設在哪裡使用");
    public static string MemoryScopeGlobal => T("In every conversation", "所有對話都用");
    public static string MemoryScopeSandbox => T("Only while this sandbox folder is in use", "只在使用這個沙盒資料夾時用");
    public static string MemoryArchiveDays => T("Treat a memory as unused after this many days without use (0 = never)", "超過幾天沒用到，就視為「久未使用」（0 = 永不）");
    public static string MemoryArchiveHelp => T(
        "An unused memory isn't given to the model in advance any more, but you can still find it and bring it back.",
        "久未使用的記憶不再事先交給模型，但你仍然找得到，也能讓它重新啟用。");
    public static string MemoryRemind => T("Remind me to tidy up", "提醒我整理記憶");
    public static string MemoryRemindNever => T("Never (I'll do it myself)", "不提醒（我自己整理）");
    public static string MemoryRemindAfter(int count) => T($"When {count} memories are waiting to be confirmed", $"累積 {count} 則「待確認」時");
    public static string MemoryConsolidateHelp => T(
        "Tidying up asks your model to merge duplicates, update what is out of date and drop what is wrong. It sends your memories and short excerpts of recent conversations to your model, after telling you how much, and nothing changes until you accept each suggestion.",
        "「整理記憶」會請你的模型合併重複的、更新過時的、淘汰錯的。會把你的記憶和最近對話的短片段送給你的模型（送之前先告訴你份量），而且每一項建議都要你同意才會套用。");

    // Toolbar, reply footer, window
    public static string MemoryTitle => T("Memory", "記憶");
    public static string MemoryTooltip(int waiting) => waiting > 0
        ? T($"Memory — {waiting} waiting to be confirmed", $"記憶 — {waiting} 則待確認")
        : MemoryTitle;
    public static string MemoryTurnedOff => T("Memory is turned off. Turn it on in Settings → Memory.", "記憶目前是關閉的。到「設定 → 記憶」開啟。");
    public static string MemoryUsedChip(int count) => T($"Used {count} memories", $"用到 {count} 則記憶");
    public static string MemoryUsedChipTooltip => T("The memories the agent was given for this conversation. Click to see them.", "這個對話開頭交給 agent 的記憶。點一下看是哪幾則。");

    /// <summary>The texts the memory page shows, by key (the page itself has no language of its own).</summary>
    public static Dictionary<string, string> MemoryPage() => new()
    {
        ["title"] = MemoryTitle,
        ["intro"] = T(
            "What the agent remembers about you across conversations. Each one was approved by you; read, edit or delete any of them.",
            "agent 跨對話記住的關於你的事。每一則都是你核准的，隨時可以修改或刪掉。"),
        ["search"] = T("Search memories and past conversations…", "搜尋記憶與過去的對話…"),
        ["add"] = T("Add a memory", "新增記憶"),
        ["consolidate"] = T("Tidy up", "整理記憶"),
        ["consolidateTip"] = T("Ask your model to merge duplicates and drop what is out of date", "請模型幫你合併重複的、淘汰過時的"),
        ["viewList"] = T("List", "清單"),
        ["viewTimeline"] = T("Timeline", "時間軸"),
        ["viewGraph"] = T("Connections", "關聯圖"),
        ["types"] = T("Kind", "種類"),
        ["states"] = T("State", "狀態"),
        ["preference"] = T("Preference", "偏好"),
        ["project"] = T("Project", "專案"),
        ["decision"] = T("Decision", "決定"),
        ["fact"] = T("Fact", "事實"),
        ["preferenceHelp"] = T("How you like things done", "你喜歡事情怎麼做"),
        ["projectHelp"] = T("About a project or piece of work", "關於某個專案或工作"),
        ["decisionHelp"] = T("A choice that was made", "已經做過的決定"),
        ["factHelp"] = T("Something worth knowing", "值得記住的事"),
        ["new"] = T("To confirm", "待確認"),
        ["settled"] = T("Confirmed", "已確認"),
        ["archived"] = T("Unused for a while", "久未使用"),
        ["newHelp"] = T("Just saved; you haven't looked it over yet", "剛存下來，你還沒看過"),
        ["settledHelp"] = T("You looked it over and want to keep it", "你看過、決定要留著"),
        ["archivedHelp"] = T("Not used for a long time, so it is no longer given to the model in advance", "很久沒用到，不再事先交給模型"),
        ["global"] = T("In every conversation", "所有對話都用"),
        ["sandbox"] = T("Only in one sandbox folder", "只在某個資料夾用"),
        ["scope"] = T("Used", "使用範圍"),
        ["type"] = T("Kind", "種類"),
        ["status"] = T("State", "狀態"),
        ["text"] = T("The memory", "內容"),
        ["uses"] = T("Used", "用過"),
        ["usedTimes"] = T("used {0} times", "用過 {0} 次"),
        ["neverUsed"] = T("not used yet", "還沒用過"),
        ["created"] = T("Saved", "存下的時間"),
        ["updated"] = T("Last changed", "上次修改"),
        ["lastUsed"] = T("Last used", "上次用到"),
        ["origin"] = T("Saved by", "是誰存的"),
        ["originUser"] = T("You", "你自己"),
        ["originAssistant"] = T("The agent, with your approval", "agent（你核准的）"),
        ["originConsolidation"] = T("Tidying up, which you accepted", "整理記憶（你同意的）"),
        ["from"] = T("From", "來自"),
        ["manual"] = T("Added by you", "你自己新增的"),
        ["sourceGone"] = T("(that conversation was deleted)", "（來源對話已刪除）"),
        ["sources"] = T("Came from these conversations", "出自這些對話"),
        ["related"] = T("Related memories", "相關的記憶"),
        ["save"] = T("Save", "儲存"),
        ["edit"] = T("Edit", "編輯"),
        ["forget"] = T("Forget", "忘掉"),
        ["forgot"] = T("Forgotten.", "已忘掉。"),
        ["undo"] = T("Undo", "還原"),
        ["saved"] = T("Saved.", "已儲存。"),
        ["revive"] = T("Use it again", "重新啟用"),
        ["archivedNote"] = T("Not used for a long time, so it isn't given to the model in advance.", "很久沒用到，所以不再事先交給模型。"),
        ["open"] = T("Open this conversation", "開啟這個對話"),
        ["conversation"] = T("Conversation", "對話"),
        ["notesFrom"] = T("Memories from this conversation", "出自這個對話的記憶"),
        ["currentConversation"] = T("The conversation you are in", "你目前所在的對話"),
        ["similar"] = T("Looks similar", "內容相近"),
        ["empty"] = T(
            "Nothing remembered yet. Ask the agent to remember something (\"remember that I prefer…\"), or add one yourself.",
            "還沒有記住任何事。你可以對 agent 說「記住，我習慣…」，或自己新增一則。"),
        ["emptyFiltered"] = T("No memory matches the filters.", "沒有符合篩選條件的記憶。"),
        ["inMemories"] = T("In memories", "記憶裡"),
        ["inConversations"] = T("In past conversations", "過去的對話裡"),
        ["nothingFound"] = T("Nothing found.", "沒有找到。"),
        ["newNote"] = T("Add a memory", "新增記憶"),
        ["newNoteHelp"] = T("One or two sentences that still make sense in a new conversation.", "一兩句話，在新的對話裡也看得懂。"),
        ["create"] = T("Add", "新增"),
        ["cancel"] = T("Cancel", "取消"),
        ["close"] = T("Close", "關閉"),
        ["off"] = MemoryTurnedOff,
        ["reminder"] = T("{0} memories are waiting to be confirmed. Tidy up?", "有 {0} 則記憶待確認，要整理一下嗎？"),
        ["estimate"] = T(
            "Tidying up sends your {0} memories and short excerpts of {1} recent conversations to your model, about {2} tokens in all{3}. Nothing changes until you accept it.",
            "整理會把你的 {0} 則記憶和最近 {1} 個對話的短片段送給你的模型，總共約 {2} token{3}。你同意之前不會改任何東西。"),
        ["run"] = T("Ask my model", "請模型整理"),
        ["asking"] = T("Asking your model…", "正在請你的模型整理…"),
        ["planTitle"] = T("Suggestions: pick what to apply", "整理的建議：勾選要套用的"),
        ["noSuggestions"] = T("Nothing to change: the memories look fine.", "沒有要改的：記憶看起來都沒問題。"),
        ["dropped"] = T("{0} suggestions were left out because they couldn't be used.", "有 {0} 則建議無法使用，已略過。"),
        ["apply"] = T("Apply selected", "套用勾選的"),
        ["applied"] = T("Applied {0}.", "已套用 {0} 項。"),
        ["opAdd"] = T("Add", "新增"),
        ["opMerge"] = T("Merge", "合併"),
        ["opUpdate"] = T("Rewrite", "改寫"),
        ["opRetire"] = T("Drop", "淘汰"),
        ["opLink"] = T("Relate", "關聯"),
        ["opSettle"] = T("Confirm", "確認"),
        ["opRetireNote"] = T("(it can be restored)", "（可以還原）"),
        ["failed"] = T("Couldn't reach your model: {0}", "無法連到你的模型：{0}"),
        ["noModel"] = T("No model is set up yet.", "還沒有設定模型。"),
        ["hint"] = T("Drag to move, scroll to zoom. Click a memory or a conversation.", "拖曳移動、滾輪縮放，點一下記憶或對話看內容。"),
        ["legendMemory"] = T("a memory", "一則記憶"),
        ["legendConversation"] = T("a conversation", "一個對話"),
        ["legendSource"] = T("came from", "出自"),
        ["legendRelated"] = T("related", "相關"),
        ["legendSimilar"] = T("looks similar", "內容相近"),
        ["legendCurrent"] = T("the one you are in", "你目前所在的"),
        ["focusOnly"] = T("Only the selected one and what it connects to", "只看選到的和相關的"),
        ["fewNodes"] = T("There is little to connect yet. The List and Timeline show more.", "目前連得起來的還很少，看「清單」和「時間軸」比較清楚。"),
        ["pickOne"] = T("Pick a memory or a conversation to see what it connects to.", "點一則記憶或一個對話，看它和什麼有關。"),
        ["highlighted"] = T("These were given to the agent in that conversation.", "這幾則是那個對話開頭交給 agent 的。"),
        ["today"] = T("Today", "今天"),
        ["yesterday"] = T("Yesterday", "昨天"),
    };
}
