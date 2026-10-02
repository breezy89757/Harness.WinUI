// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Harness.Core.Config;
using Harness.Core.Files;
using Harness.Core.Skills;

namespace Harness.Core.Tools;

/// <summary>
/// Everything the agent can call: built-in tools (sandboxed file access, MCP management, image
/// generation) plus tools from MCP servers.
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
            tools.AddRange(FileTools.Create(_sandbox, _approver, _permissions));
        tools.AddRange(McpServerManager.CreateAgentTools(Mcp, _approver, _permissions));
        if (_image is not null)
            tools.Add(ImageGenerationTool.Create(_image, () => ImageQuality));

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
