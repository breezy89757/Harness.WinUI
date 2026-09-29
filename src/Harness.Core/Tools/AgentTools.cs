// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Harness.Core.Files;

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

        Rebuild();
    }

    private void Rebuild()
    {
        if (_approver is null || _permissions is null)
            return;

        var tools = new List<AITool>();
        if (_sandbox is not null)
            tools.AddRange(FileTools.Create(_sandbox, _approver, _permissions));
        tools.AddRange(McpServerManager.CreateAgentTools(Mcp, _approver, _permissions));
        if (_image is not null)
            tools.Add(ImageGenerationTool.Create(_image, () => ImageQuality));

        Volatile.Write(ref _builtIn, tools);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => Mcp.DisposeAsync();
}
