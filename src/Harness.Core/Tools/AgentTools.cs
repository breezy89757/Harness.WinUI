// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
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

    /// <summary>Snapshot read by the chat session at the start of every turn.</summary>
    public IReadOnlyList<AITool> All => [.. Volatile.Read(ref _builtIn), .. Mcp.Tools];

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
        return own is null ? memories : memories is null ? own : own + "\n\n" + memories;
    }

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
            _sandbox = new Sandbox(folder);
        }

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

        var tools = new List<AITool>();
        tools.AddRange(SkillCatalog.CreateTools(() => Skills.Where(s => IsSkillEnabled(s.Name)).ToList()));
        if (_sandbox is not null)
        {
            tools.AddRange(FileTools.Create(_sandbox, _approver, _permissions));
            tools.Add(CommandTool.Create(_sandbox, _approver, _permissions));
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
        if (_image is not null)
            tools.Add(ImageGenerationTool.Create(_image, () => ImageQuality));
        if (_applyAppearance is { } applyAppearance)
            tools.Add(AppearanceTool.Create(_approver, _permissions, _toolbarButtons, applyAppearance));

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
        return Mcp.DisposeAsync();
    }
}
