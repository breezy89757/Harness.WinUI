// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Core.Memory;

/// <summary>What kind of thing a memory is; shapes how it is shown and grouped.</summary>
public enum MemoryType
{
    /// <summary>How the user likes things done.</summary>
    Preference,

    /// <summary>Something about a project or piece of work.</summary>
    Project,

    /// <summary>A choice that was made, so it isn't argued again.</summary>
    Decision,

    /// <summary>A plain fact worth keeping.</summary>
    Fact,
}

/// <summary>How settled a memory is. "Archived" is never stored: it is what a long-unused memory counts as (see <see cref="MemoryStore.IsArchived"/>).</summary>
public enum MemoryStatus
{
    /// <summary>Just saved, not looked over yet.</summary>
    New,

    /// <summary>Looked over (a consolidation the user approved, or edited by the user).</summary>
    Settled,
}

/// <summary>Where a memory came from: a message of a saved conversation.</summary>
public sealed record MemorySource(string ConversationId, long Seq);

/// <param name="Scope">"global", or "sandbox:" and a folder path: only used when that folder is the sandbox.</param>
/// <param name="Origin">Who wrote it: "user" (in the memory window), "assistant" (the agent, with approval) or "consolidation".</param>
/// <param name="Related">Ids of related memories (kept symmetric by the store).</param>
public sealed record MemoryItem(
    string Id, MemoryType Type, string Scope, MemoryStatus Status, string Origin,
    DateTimeOffset Created, DateTimeOffset Updated, DateTimeOffset? LastUsed, int Uses,
    IReadOnlyList<MemorySource> Sources, IReadOnlyList<string> Related, string Text)
{
    public const string GlobalScope = "global";

    public static string SandboxScope(string folder) => "sandbox:" + folder.Trim();

    /// <summary>The folder of a sandbox-scoped memory, or null for a global one.</summary>
    public string? SandboxFolder => Scope.StartsWith("sandbox:", StringComparison.Ordinal) ? Scope["sandbox:".Length..] : null;
}
