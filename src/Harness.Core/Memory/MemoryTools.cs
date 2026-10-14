// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Harness.Core.Config;
using Harness.Core.History;
using Harness.Core.Tools;

namespace Harness.Core.Memory;

/// <summary>
/// The agent's memory tools: remember, update_memory and forget (each asks the user first, and is refused in read-only
/// mode) and search_memory (reads only). Offered only while memory is turned on in Settings.
/// </summary>
public static class MemoryTools
{
    public const string ServerName = "memory";

    /// <param name="sandboxRoot">The sandbox folder right now (memories of other folders aren't shown).</param>
    /// <param name="conversation">The conversation the agent is in: its id, and the position its next user message will have (where a new memory came from).</param>
    public static IEnumerable<AITool> Create(
        MemoryStore store, IToolApprover approver, ToolPermissionStore permissions,
        Func<string?> sandboxRoot, Func<(string? Id, long NextSeq)> conversation)
    {
        AITool Gate(AIFunction function, bool readOnly) =>
            new ApprovalGatedFunction(function, ServerName, function.Name, readOnly, approver, permissions);

        yield return Gate(AIFunctionFactory.Create(
            (
                [Description("The note: one or two self-contained sentences in the user's language, that still make sense without this conversation. Never include keys, passwords or tokens.")] string text,
                [Description("preference (how the user likes things done), project (about a project or piece of work), decision (a choice that was made) or fact.")] string type = "fact",
                [Description("global (everywhere) or sandbox (only while the current sandbox folder is in use). Leave empty to use the user's default.")] string? scope = null) =>
                    Remember(store, sandboxRoot(), conversation(), text, type, scope),
            "remember",
            "Save a short note to the user's long-term memory so it is available in future conversations. Use it when the user asks you to remember something, " +
            "or states a lasting preference, rule or decision that will matter later. Don't use it for one-off details, for what is only true in this conversation, " +
            "or for anything secret. Check search_memory first so you don't save the same thing twice. The user is asked to approve every note."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            (
                [Description("The memory's id, as shown by search_memory.")] string id,
                [Description("The new text of the note.")] string text) => UpdateMemory(store, id, text),
            "update_memory",
            "Replace the text of a saved memory that is out of date or wrong (the old version is kept so it can be undone). The user is asked to approve it."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            ([Description("The memory's id, as shown by search_memory.")] string id) => Forget(store, id),
            "forget",
            "Forget a saved memory (the user can still restore it). Use it when the user asks you to forget something or a memory is plainly wrong. The user is asked to approve it."), readOnly: false);

        yield return Gate(AIFunctionFactory.Create(
            (
                [Description("Words to look for in the notes (any of them); empty lists the most recent notes.")] string? query = null,
                [Description("At most this many notes (default 8).")] int limit = 8) => Search(store, sandboxRoot(), query, limit),
            "search_memory",
            "Search the user's long-term memory (notes saved from earlier conversations). The most relevant ones are already given to you at the start of a conversation; use this to look for others."), readOnly: true);
    }

    private static string Remember(MemoryStore store, string? sandbox, (string? Id, long NextSeq) conversation, string text, string type, string? scope)
    {
        try
        {
            var kind = Enum.TryParse<MemoryType>(type, ignoreCase: true, out var parsed) ? parsed : MemoryType.Fact;
            var wanted = string.IsNullOrWhiteSpace(scope) ? AppPreferences.Load().MemoryDefaultScope : scope;
            // A sandbox-scoped note needs a sandbox; without one it can only be global.
            var itemScope = wanted.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sandbox)
                ? MemoryItem.SandboxScope(sandbox)
                : MemoryItem.GlobalScope;
            var sources = conversation.Id is { } id ? new[] { new MemorySource(id, conversation.NextSeq) } : [];
            var item = store.Add(text, kind, itemScope, "assistant", sources);
            return $"Saved as memory {item.Id} ({item.Type.ToString().ToLowerInvariant()}, {(item.SandboxFolder is null ? "global" : "this sandbox folder")}).";
        }
        catch (ArgumentException ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private static string UpdateMemory(MemoryStore store, string id, string text)
    {
        try
        {
            return store.SetText(id, text) is null ? $"Error: no memory with id {id}." : $"Updated memory {id}.";
        }
        catch (ArgumentException ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private static string Forget(MemoryStore store, string id) =>
        store.Delete(id) ? $"Forgot memory {id}. The user can restore it in the memory window." : $"Error: no memory with id {id}.";

    private static string Search(MemoryStore store, string? sandbox, string? query, int limit)
    {
        var items = store.Search(query, sandbox, limit, AppPreferences.Load().MemoryArchiveDays);
        if (items.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "There are no saved memories." : "No memories match: " + query;

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append('[').Append(item.Id).Append("] (").Append(item.Type.ToString().ToLowerInvariant()).Append(", ")
                .Append(item.Updated.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(") ")
                .AppendLine(item.Text);
            foreach (var source in item.Sources.Take(2).Where(s => s.Seq > 0))
                sb.Append("    from: ").AppendLine(HistorySearchTool.Link(source.ConversationId, source.Seq));
        }
        return sb.ToString().TrimEnd();
    }
}
