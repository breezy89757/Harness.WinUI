// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Harness.Core.Config;
using Harness.Core.Observability;

namespace Harness.Core.Tools;

public enum ToolApprovalDecision
{
    Deny,
    AllowOnce,
    AlwaysAllow,
    /// <summary>Like AllowOnce, and the same action is allowed without asking until the conversation changes.</summary>
    AllowForSession,
}

/// <param name="CallId">Matches <see cref="Agent.ToolCallStarted.CallId"/> so the UI can attach the prompt to that step.</param>
/// <param name="CanAlwaysAllow">False for tools that change Harness.WinUI's own configuration — those are asked every time.</param>
/// <param name="AlwaysAllowScope">What "Always allow" would cover when it's narrower than the whole tool (e.g. commands starting with "uv run").</param>
public sealed record ToolApprovalRequest(
    string CallId, string ServerName, string ToolName, IReadOnlyDictionary<string, object?> Arguments, bool CanAlwaysAllow,
    string? AlwaysAllowScope = null);

/// <summary>Asks the user whether a tool with side effects may run. Implemented by the UI.</summary>
public interface IToolApprover
{
    Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What may run without asking. "Always allow" choices are persisted, keyed by server + tool, in permissions.json in the
/// data folder (<see cref="AppPaths"/>); "allow for this conversation" choices live in memory until the conversation
/// changes; and read-only mode, when on, stops everything that changes something, whatever was allowed.
/// </summary>
public sealed class ToolPermissionStore
{
    private readonly string _path;
    private readonly HashSet<string> _alwaysAllowed;
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private volatile bool _readOnlyMode;

    public ToolPermissionStore(string? path = null)
    {
        _path = path ?? AppPaths.Combine("permissions.json");

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

    /// <summary>On: tools that change files or run commands are refused outright (reading and searching still work). Not remembered across launches.</summary>
    public bool ReadOnlyMode
    {
        get => _readOnlyMode;
        set
        {
            _readOnlyMode = value;
            if (value)
                _autoApprove = false;
        }
    }

    private volatile bool _autoApprove;

    /// <summary>
    /// "YOLO": what the user could have set to "always allow" runs without asking, commands that pipe, chain or redirect included.
    /// Tools that change the app's own configuration (adding an MCP server, the look of the app) are still asked about every time.
    /// Not remembered across launches, and exclusive with <see cref="ReadOnlyMode"/>.
    /// </summary>
    public bool AutoApprove
    {
        get => _autoApprove;
        set
        {
            _autoApprove = value;
            if (value)
                _readOnlyMode = false;
        }
    }

    private string? _scope;

    /// <summary>
    /// The project folder whose "always allow" choices apply on top of the global ones (Code mode), or null. A choice made while
    /// a folder is set belongs to that folder only: allowing a command in one project doesn't allow it in the next.
    /// </summary>
    public string? Scope
    {
        get
        {
            lock (_lock)
                return _scope;
        }
        set
        {
            lock (_lock)
                _scope = string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\\', '/');
        }
    }

    public bool IsAlwaysAllowed(string serverName, string toolName)
    {
        lock (_lock)
            return _alwaysAllowed.Contains(Key(serverName, toolName)) || (_scope is not null && _alwaysAllowed.Contains(ScopedKey(_scope, serverName, toolName)));
    }

    public bool IsAllowedThisConversation(string serverName, string toolName)
    {
        lock (_lock)
            return _session.Contains(Key(serverName, toolName));
    }

    public void AllowForConversation(string serverName, string toolName)
    {
        lock (_lock)
            _session.Add(Key(serverName, toolName));
    }

    /// <summary>Forgets the "allow for this conversation" choices: a new or another conversation starts asking again.</summary>
    public void ClearConversationAllowances()
    {
        lock (_lock)
            _session.Clear();
    }

    public void AllowAlways(string serverName, string toolName)
    {
        lock (_lock)
        {
            if (_alwaysAllowed.Add(_scope is null ? Key(serverName, toolName) : ScopedKey(_scope, serverName, toolName)))
                Save();
        }
    }

    /// <summary>Everything that's always allowed everywhere, as (server, tool) — the tool may carry a scope, e.g. "run_command uv run".</summary>
    public IReadOnlyList<(string ServerName, string ToolName)> List()
    {
        lock (_lock)
            return _alwaysAllowed.Where(k => !k.Contains('|')).Order().Select(k => k.Split('/', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
    }

    /// <summary>Everything that's always allowed: the global choices (folder null) and those made for a project folder.</summary>
    public IReadOnlyList<(string? Folder, string ServerName, string ToolName)> ListAll()
    {
        lock (_lock)
        {
            var all = new List<(string? Folder, string ServerName, string ToolName)>();
            foreach (var key in _alwaysAllowed.Order())
            {
                var bar = key.IndexOf('|');
                var folder = bar < 0 ? null : key[..bar];
                var parts = key[(bar + 1)..].Split('/', 2);
                if (parts.Length == 2)
                    all.Add((folder, parts[0], parts[1]));
            }
            return all;
        }
    }

    /// <summary>Asks again from now on (a choice made for a project folder, or null for a global one).</summary>
    public void Remove(string? folder, string serverName, string toolName)
    {
        lock (_lock)
        {
            if (_alwaysAllowed.Remove(folder is null ? Key(serverName, toolName) : ScopedKey(folder, serverName, toolName)))
                Save();
        }
    }

    /// <summary>Asks again from now on.</summary>
    public void Remove(string serverName, string toolName)
    {
        lock (_lock)
        {
            if (_alwaysAllowed.Remove(Key(serverName, toolName)))
                Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_alwaysAllowed.Order().ToArray(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Key(string serverName, string toolName) => $"{serverName}/{toolName}";

    private static string ScopedKey(string folder, string serverName, string toolName) => $"{folder}|{serverName}/{toolName}";
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
    private readonly bool _readsPrivateData;
    private readonly bool _canAlwaysAllow;
    private readonly IToolApprover _approver;
    private readonly ToolPermissionStore _permissions;
    private readonly Func<AIFunctionArguments, string?>? _scope;

    /// <param name="scope">
    /// When set, "Always allow" covers only calls with the same scope (e.g. commands starting with "uv run"),
    /// remembered as "tool scope"; a call whose scope is null is asked every time.
    /// </param>
    /// <param name="readsPrivateData">
    /// For a tool that changes no files and runs no commands, yet the user should be asked every time: it returns something they
    /// may not want sent to the model (their past conversations), or it changes how the app looks. It is asked about like a tool
    /// that changes things, yet read-only mode doesn't refuse it.
    /// </param>
    public ApprovalGatedFunction(
        AIFunction inner, string serverName, string originalName, bool readOnly,
        IToolApprover approver, ToolPermissionStore permissions, bool canAlwaysAllow = true,
        Func<AIFunctionArguments, string?>? scope = null, bool readsPrivateData = false)
        : base(inner)
    {
        _readsPrivateData = readsPrivateData;
        _scope = scope;
        _serverName = serverName;
        _originalName = originalName;
        _readOnly = readOnly;
        _canAlwaysAllow = canAlwaysAllow;
        _approver = approver;
        _permissions = permissions;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The current span is this call's execute_tool span (when recording): say where the tool comes from.
        Activity.Current?.SetTag(Telemetry.TagToolSource, _serverName);

        // Read-only mode refuses whatever changes something: no question, whatever was allowed before.
        if (!_readOnly && _permissions.ReadOnlyMode)
            return "Read-only mode is on, so nothing that changes files or runs commands is allowed right now. Don't try it again or work around it: tell the user you can't do this while read-only mode is on, and that they can turn it off with the lock button in the toolbar.";

        var scope = _scope?.Invoke(arguments);
        var permission = _scope is null ? _originalName : scope is null ? null : $"{_originalName} {scope}";
        var canAlwaysAllow = _canAlwaysAllow && permission is not null;
        var skipAsking = _permissions.AutoApprove && _canAlwaysAllow;
        if ((!_readOnly || _readsPrivateData) && !skipAsking && !(canAlwaysAllow && (_permissions.IsAlwaysAllowed(_serverName, permission!) || _permissions.IsAllowedThisConversation(_serverName, permission!))))
        {
            var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
            var request = new ToolApprovalRequest(callId, _serverName, Name, new Dictionary<string, object?>(arguments), canAlwaysAllow, scope);

            ToolApprovalDecision decision;
            using (var waiting = Telemetry.Source.StartActivity(Telemetry.OpApproval))
            {
                waiting?.SetTag(Telemetry.TagToolName, Name);
                decision = await _approver.RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false);
                waiting?.SetTag(Telemetry.TagDecision, decision.ToString());
            }
            if (decision == ToolApprovalDecision.Deny)
                return "The user declined to run this tool. Do not retry it; ask the user how they would like to proceed.";

            if (decision == ToolApprovalDecision.AlwaysAllow && canAlwaysAllow)
                _permissions.AllowAlways(_serverName, permission!);
            else if (decision == ToolApprovalDecision.AllowForSession && canAlwaysAllow)
                _permissions.AllowForConversation(_serverName, permission!);
        }

        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("missing a value for the required parameter", StringComparison.Ordinal))
        {
            // The model left out an argument; say which, in words it can act on, rather than a .NET message.
            return MissingArguments(arguments);
        }
    }

    private string MissingArguments(AIFunctionArguments arguments)
    {
        var missing = new List<string>();
        if (JsonSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(n => n.GetString()).OfType<string>())
            {
                if (!arguments.ContainsKey(name) || arguments[name] is null)
                    missing.Add(name);
            }
        }
        return missing.Count > 0
            ? $"Error: the call to {Name} is missing the required argument(s): {string.Join(", ", missing)}. Call it again with every required argument."
            : $"Error: the call to {Name} is missing a required argument. Call it again with every required argument.";
    }
}
