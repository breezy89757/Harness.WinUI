// Yoke — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Yoke.Core.Tools;

public enum ToolApprovalDecision
{
    Deny,
    AllowOnce,
    AlwaysAllow,
}

/// <param name="CallId">Matches <see cref="Agent.ToolCallStarted.CallId"/> so the UI can attach the prompt to that step.</param>
/// <param name="CanAlwaysAllow">False for tools that change Yoke's own configuration — those are asked every time.</param>
public sealed record ToolApprovalRequest(
    string CallId, string ServerName, string ToolName, IReadOnlyDictionary<string, object?> Arguments, bool CanAlwaysAllow);

/// <summary>Asks the user whether a tool with side effects may run. Implemented by the UI.</summary>
public interface IToolApprover
{
    Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Persisted "always allow" choices, keyed by server + tool, in %LOCALAPPDATA%\Yoke\permissions.json.</summary>
public sealed class ToolPermissionStore
{
    private readonly string _path;
    private readonly HashSet<string> _alwaysAllowed;
    private readonly Lock _lock = new();

    public ToolPermissionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke", "permissions.json");

        _alwaysAllowed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) is { } saved)
                _alwaysAllowed.UnionWith(saved);
        }
        catch (JsonException)
        {
            // Unreadable file: start empty; the user just gets asked again.
        }
    }

    public bool IsAlwaysAllowed(string serverName, string toolName)
    {
        lock (_lock)
            return _alwaysAllowed.Contains(Key(serverName, toolName));
    }

    public void AllowAlways(string serverName, string toolName)
    {
        lock (_lock)
        {
            if (!_alwaysAllowed.Add(Key(serverName, toolName)))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_alwaysAllowed.Order().ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Key(string serverName, string toolName) => $"{serverName}/{toolName}";
}

/// <summary>
/// Wraps a tool so that, unless it's read-only or already always-allowed, invoking it first asks
/// <see cref="IToolApprover"/>. Denial is returned to the model as the tool result (rather than
/// thrown) so it can explain and ask the user instead of retrying blindly.
/// </summary>
public sealed class ApprovalGatedFunction : DelegatingAIFunction
{
    private readonly string _serverName;
    private readonly string _originalName;
    private readonly bool _readOnly;
    private readonly bool _canAlwaysAllow;
    private readonly IToolApprover _approver;
    private readonly ToolPermissionStore _permissions;

    public ApprovalGatedFunction(
        AIFunction inner, string serverName, string originalName, bool readOnly,
        IToolApprover approver, ToolPermissionStore permissions, bool canAlwaysAllow = true)
        : base(inner)
    {
        _serverName = serverName;
        _originalName = originalName;
        _readOnly = readOnly;
        _canAlwaysAllow = canAlwaysAllow;
        _approver = approver;
        _permissions = permissions;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!_readOnly && !(_canAlwaysAllow && _permissions.IsAlwaysAllowed(_serverName, _originalName)))
        {
            var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
            var request = new ToolApprovalRequest(callId, _serverName, Name, new Dictionary<string, object?>(arguments), _canAlwaysAllow);

            var decision = await _approver.RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false);
            if (decision == ToolApprovalDecision.Deny)
                return "The user declined to run this tool. Do not retry it; ask the user how they would like to proceed.";

            if (decision == ToolApprovalDecision.AlwaysAllow && _canAlwaysAllow)
                _permissions.AllowAlways(_serverName, _originalName);
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
