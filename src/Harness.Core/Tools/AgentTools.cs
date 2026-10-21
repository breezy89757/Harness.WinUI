// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Harness.Core.Agent;
using Harness.Core.Config;
using Harness.Core.Files;
using Harness.Core.Memory;
using Harness.Core.Skills;

namespace Harness.Core.Tools;

/// <summary>
/// Everything the agent can call: built-in tools (sandboxed file access, commands, MCP management, web,
/// image generation) plus tools from MCP servers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentTools : IAsyncDisposable
{
    private IToolApprover? _approver;
    private ToolPermissionStore? _permissions;
    private ImageGenerationSettings? _image;
    private Sandbox? _sandbox;
    private IReadOnlyList<AITool> _builtIn = [];

    public McpHost Mcp { get; } = new();

    // Tools that only read. Their calls in one reply run at the same time; every other tool (and every MCP tool) runs alone, after the
    // calls before it (see ToolGate).
    private static readonly HashSet<string> s_overlapping = new(
        ["read_file", "list_directory", "find_files", "search_text", WebFetchTool.Name, HelperAgentTool.Name, BackgroundTools.OutputName],
        StringComparer.Ordinal);

    private readonly ToolGate _gate = new();

    /// <summary>Snapshot read by the chat session at the start of every turn.</summary>
    public IReadOnlyList<AITool> All =>
    [
        .. Volatile.Read(ref _builtIn).Select(t => _gate.Wrap(t, shared: t is AIFunction f && s_overlapping.Contains(f.Name))),
        .. Mcp.Tools.Select(t => _gate.Wrap(t, shared: false)),
    ];

    /// <summary>File, MCP-management and image tools (everything that isn't from an MCP server).</summary>
    public int BuiltInToolCount => Volatile.Read(ref _builtIn).Count;

    /// <summary>Default generate_image quality ("low" / "medium" / "high"), read on every call.</summary>
    public string ImageQuality { get; set; } = "low";

    /// <summary>Where the file tools work, or null when they're off.</summary>
    public string? SandboxRoot => _sandbox?.Root;

    /// <summary>
    /// What is added to the model's instructions right now: the user's own (Settings plus the sandbox folder's AGENTS.md) and, when
    /// memory is on, their memories (null when there is nothing to add).
    /// </summary>
    /// <param name="firstUserMessage">The first thing the user said in this conversation, which decides which memories matter most.</param>
    public string? ExtraInstructions(string? firstUserMessage)
    {
        var own = Harness.Core.Agent.CustomInstructions.Compose(AppPreferences.Load().CustomInstructions, SandboxRoot);
        var memories = _memory ? MemoryFor(firstUserMessage) : null;
        var coding = _mode == AppMode.Chat && SandboxRoot is not null && Harness.Core.Agent.CodingInstructions.LooksLikeProject(SandboxRoot)
            ? Harness.Core.Agent.CodingInstructions.Text
            : null;
        var deck = _mode == AppMode.Chat ? DeckRulesText() : null;
        var all = string.Join("\n\n", new[] { coding, deck, own, memories }.Where(p => p is not null));
        return all.Length == 0 ? null : all;
    }

    private readonly Harness.Core.Artifacts.BrandThemeStore _themes = new();
    private (string Id, DateTime Stamp, DateOnly Day, string? Text)? _deckRules;

    /// <summary>What the model is told about the company presentation theme in use (null when there is none); read again only when the theme's files change.</summary>
    private string? DeckRulesText()
    {
        var id = AppPreferences.Load().SlideTheme;
        if (string.IsNullOrEmpty(id))
            return null;

        var stamp = new[] { Path.Combine(_themes.FolderOf(id), Harness.Core.Artifacts.BrandThemeStore.ThemeFile), _themes.RulesPathOf(id) }
            .Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue).Max();
        var day = DateOnly.FromDateTime(DateTime.Now);
        if (_deckRules is { } cached && cached.Id == id && cached.Stamp == stamp && cached.Day == day)
            return cached.Text;

        var theme = _themes.Resolve(id);
        var text = Harness.Core.Artifacts.DeckRules.Compose(theme, _themes.ReadRules(id), day);
        _deckRules = (id, stamp, day, text);
        return text;
    }

    #region Mode

    private AppMode _mode = AppMode.Chat;

    /// <summary>Chat (everything, as ever) or Code (a coding agent in a project folder: its own instructions, no Office or image tools).</summary>
    public AppMode Mode => _mode;

    public void SetMode(AppMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        Harness.Core.Agent.CodingInstructions.Forget();
        ApplyMcpFilter();
        Rebuild();
    }

    /// <summary>In Code mode only the MCP servers the user picked are offered (all of them when none were picked); Chat mode offers all.</summary>
    public void ApplyMcpFilter()
    {
        var chosen = _mode == AppMode.Code ? AppPreferences.Load().CodeMcpServers : null;
        Mcp.SetServerFilter(chosen is null ? null : name => chosen.Contains(name, StringComparer.Ordinal));
    }

    /// <summary>The instructions that replace the built-in ones: Code mode's own; null in Chat mode.</summary>
    public string? BaseInstructions() =>
        _mode == AppMode.Code ? CodingPrompt.Build(tasks: true, helpers: _helpers is not null, background: true) : null;

    #endregion

    #region Memory

    private bool _memory;
    private MemoryStore? _memoryStore;
    private readonly Dictionary<string, string?> _memorySnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _memorySnapshotIds = new(StringComparer.Ordinal);
    private readonly Lock _memoryLock = new();

    /// <summary>The user's long-term memory (the same store the memory window works on).</summary>
    public MemoryStore Memory => _memoryStore ??= new MemoryStore();

    /// <summary>Turns the agent's memory on or off: its tools, and the memories put in front of the model.</summary>
    public void SetMemory(bool enabled)
    {
        if (_memory == enabled)
            return;
        _memory = enabled;
        Rebuild();
    }

    /// <summary>
    /// The memories for this conversation, chosen once when the conversation first asks and then kept as they were, so the
    /// start of every later request stays the same (a prompt cache only helps while it does). A memory saved meanwhile
    /// is found by search_memory, and is in front of the model from the next conversation on.
    /// </summary>
    private string? MemoryFor(string? firstUserMessage)
    {
        var key = CurrentConversationId() ?? "";
        lock (_memoryLock)
        {
            if (_memorySnapshots.TryGetValue(key, out var kept))
                return kept;

            var preferences = AppPreferences.Load();
            string? block = null;
            IReadOnlyList<string> ids = [];
            try
            {
                var items = Memory.Select(SandboxRoot, firstUserMessage, preferences.MemoryBudgetTokens * 3, preferences.MemoryArchiveDays);
                block = MemoryInstructions.Compose(items);
                ids = [.. items.Select(i => i.Id)];
                if (items.Count > 0)
                    Memory.Touch(ids);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The memory folder can't be read: chat without memories rather than failing.
            }

            if (_memorySnapshots.Count > 50)
            {
                _memorySnapshots.Clear();
                _memorySnapshotIds.Clear();
            }
            _memorySnapshots[key] = block;
            _memorySnapshotIds[key] = ids;
            return block;
        }
    }

    /// <summary>The memories this conversation was given at its start (empty when there were none, or memory is off).</summary>
    public IReadOnlyList<string> MemoryUsedBy(string? conversationId)
    {
        lock (_memoryLock)
            return _memory && conversationId is not null && _memorySnapshotIds.TryGetValue(conversationId, out var ids) ? ids : [];
    }

    #endregion

    private bool _searchHistory;

    /// <summary>The id of the conversation being held, or null; the history search leaves that conversation out of its results.</summary>
    public Func<string?> CurrentConversationId { get; set; } = () => null;

    /// <summary>The conversation the agent is in and where its next user message will be saved (what a new memory is traced back to).</summary>
    private (string? Id, long NextSeq) CurrentConversation()
    {
        var id = CurrentConversationId();
        if (id is null)
            return (null, 0);
        try
        {
            return (id, new Harness.Core.History.ConversationStore().NextSeq(id));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return (id, 0);
        }
    }

    /// <summary>Offers (or stops offering) search_history, which looks through the user's past conversations.</summary>
    public void SetHistorySearch(bool enabled)
    {
        if (_searchHistory == enabled)
            return;
        _searchHistory = enabled;
        Rebuild();
    }

    /// <summary>Programs the agent started and left running (Code mode); stopped when the app closes.</summary>
    public BackgroundProcesses Background { get; } = new();

    private bool _allowInstructionsWrite;

    /// <summary>While true, the agent may write AGENTS.md (the user asked for it with /init); false otherwise, so a page or file can't talk it into rewriting its own instructions.</summary>
    public bool AllowInstructionsWrite
    {
        get => _allowInstructionsWrite;
        set
        {
            _allowInstructionsWrite = value;
            if (_sandbox is not null)
                _sandbox.AllowInstructionsWrite = value;
        }
    }

    /// <summary>What the file tools changed, per message, so it can be undone.</summary>
    public FileCheckpoints Checkpoints { get; } = new();

    /// <summary>Code mode's task list for the conversation on screen (set by update_tasks, shown above the message box).</summary>
    public TaskList Tasks { get; } = new();

    private Func<IChatClient>? _helpers;

    /// <summary>A helper agent finished: what its model calls used (so the conversation's cost includes them).</summary>
    public event Action<UsageReported>? HelperUsage;

    /// <summary>A helper agent started, made a tool call or finished, for the step that shows it.</summary>
    public event Action<HelperEvent>? HelperProgress;

    /// <summary>The helpers that are running (stop one with <see cref="HelperRegistry.Stop"/>).</summary>
    public HelperRegistry Helpers { get; } = new();

    /// <summary>The reasoning effort a helper's model calls use: low when the user picked an effort for the main agent (a read-only search doesn't need more), otherwise the model's default.</summary>
    public Func<ReasoningEffort?>? HelperEffort { get; set; }

    /// <summary>Lets Code mode start read-only helper agents (spawn_agent); <paramref name="client"/> makes a chat client for the current provider.</summary>
    public void SetHelpers(Func<IChatClient>? client)
    {
        _helpers = client;
        Rebuild();
    }

    private Func<string, string, Task<string>>? _applyAppearance;
    private IReadOnlyList<string> _toolbarButtons = [];

    /// <summary>Lets the agent change the app's look (change_appearance); <paramref name="apply"/> does the change in the window.</summary>
    public void SetAppearance(IReadOnlyList<string> toolbarButtons, Func<string, string, Task<string>> apply)
    {
        _toolbarButtons = toolbarButtons;
        _applyAppearance = apply;
        Rebuild();
    }

    /// <summary>Raised when the built-in tool set changes (sandbox or image settings); MCP changes raise <see cref="McpHost.Changed"/>.</summary>
    public event EventHandler? Changed;

    public void Configure(IToolApprover approver, ToolPermissionStore permissions)
    {
        _approver = approver;
        _permissions = permissions;
        Mcp.Configure(approver, permissions);
        Rebuild();
    }

    /// <summary>Registers (or removes, when null) the image generation tool — follows the provider settings.</summary>
    public void SetImageGeneration(ImageGenerationSettings? settings)
    {
        _image = settings;
        Rebuild();
    }

    /// <summary>Points the file tools at <paramref name="folder"/> (created if missing); null or empty turns them off.</summary>
    public void SetSandbox(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            _sandbox = null;
        }
        else
        {
            Directory.CreateDirectory(folder);
            _sandbox = new Sandbox(folder) { AllowInstructionsWrite = _allowInstructionsWrite };
        }

        Harness.Core.Agent.CodingInstructions.Forget();

        // The sandbox may hold project skills (.agents/skills).
        RefreshSkills();
    }

    #region Skills

    private IReadOnlyList<Skill> _skills = [];
    private IReadOnlyList<SkillProblem> _skillProblems = [];
    private readonly List<FileSystemWatcher> _skillWatchers = [];
    private Timer? _skillRefreshTimer;

    /// <summary>Every skill found (enabled or not); see <see cref="SkillCatalog.Roots"/>.</summary>
    public IReadOnlyList<Skill> Skills => Volatile.Read(ref _skills);

    /// <summary>SKILL.md files that couldn't be used, and why.</summary>
    public IReadOnlyList<SkillProblem> SkillProblems => Volatile.Read(ref _skillProblems);

    public static bool IsSkillEnabled(string name) => AppPreferences.Load().DisabledSkills?.Contains(name) != true;

    public void SetSkillEnabled(string name, bool enabled)
    {
        var preferences = AppPreferences.Load();
        var disabled = new HashSet<string>(preferences.DisabledSkills ?? [], StringComparer.Ordinal);
        if (enabled ? !disabled.Remove(name) : !disabled.Add(name))
            return;
        (preferences with { DisabledSkills = disabled.Count == 0 ? null : [.. disabled.Order()] }).Save();
        Rebuild();
    }

    /// <summary>Re-reads the skill folders and watches them, so adding or editing a skill applies without a restart.</summary>
    public void RefreshSkills()
    {
        Directory.CreateDirectory(SkillCatalog.AppSkillsFolder);
        var (skills, problems) = SkillCatalog.Discover(SandboxRoot);
        Volatile.Write(ref _skills, skills);
        Volatile.Write(ref _skillProblems, problems);

        lock (_skillWatchers)
        {
            foreach (var watcher in _skillWatchers)
                watcher.Dispose();
            _skillWatchers.Clear();
            foreach (var (folder, _) in SkillCatalog.Roots(SandboxRoot).Where(r => Directory.Exists(r.Folder)))
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                watcher.Changed += OnSkillFolderChanged;
                watcher.Created += OnSkillFolderChanged;
                watcher.Deleted += OnSkillFolderChanged;
                watcher.Renamed += OnSkillFolderChanged;
                _skillWatchers.Add(watcher);
            }
        }
        Rebuild();
    }

    // Editors save in bursts (temp file, rename, write): refresh once things settle.
    private void OnSkillFolderChanged(object sender, FileSystemEventArgs e)
    {
        _skillRefreshTimer?.Dispose();
        _skillRefreshTimer = new Timer(_ => RefreshSkills(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    #endregion

    private void Rebuild()
    {
        if (_approver is null || _permissions is null)
            return;

        var code = _mode == AppMode.Code;
        var tools = new List<AITool>();
        tools.AddRange(SkillCatalog.CreateTools(() => Skills.Where(s => IsSkillEnabled(s.Name)).ToList()));
        if (_sandbox is not null)
        {
            tools.AddRange(FileTools.Create(_sandbox, _approver, _permissions, includeOffice: !code, checkpoints: Checkpoints));
            tools.Add(CommandTool.Create(_sandbox, _approver, _permissions, hasBackground: code));
            if (code)
                tools.AddRange(BackgroundTools.Create(_sandbox, Background, _approver, _permissions));
        }
        tools.AddRange(McpServerManager.CreateAgentTools(Mcp, _approver, _permissions));
        tools.Add(WebFetchTool.Create(_approver));
        if (_searchHistory)
        {
            try
            {
                tools.Add(HistorySearchTool.Create(new Harness.Core.History.ConversationStore(), _approver, _permissions, () => CurrentConversationId()));
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // History can't be opened (the app then doesn't save conversations either): there is nothing to search.
            }
        }
        if (_memory)
        {
            tools.AddRange(MemoryTools.Create(Memory, _approver, _permissions, () => SandboxRoot, CurrentConversation));
        }
        if (_image is not null && !code)
            tools.Add(ImageGenerationTool.Create(_image, () => ImageQuality));
        if (_applyAppearance is { } applyAppearance)
            tools.Add(AppearanceTool.Create(_approver, _permissions, _toolbarButtons, applyAppearance));
        if (code)
            tools.Add(TaskTools.Create(Tasks));
        if (code && _helpers is { } helperClient && _sandbox is not null)
            tools.Add(HelperAgentTool.Create(helperClient, () => _sandbox, _approver, _permissions, u => HelperUsage?.Invoke(u), e => HelperProgress?.Invoke(e), () => HelperEffort?.Invoke(), Helpers));

        Volatile.Write(ref _builtIn, tools);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        lock (_skillWatchers)
        {
            foreach (var watcher in _skillWatchers)
                watcher.Dispose();
            _skillWatchers.Clear();
        }
        _skillRefreshTimer?.Dispose();
        Background.Dispose(); // programs the agent left running stop with the app
        return Mcp.DisposeAsync();
    }
}
