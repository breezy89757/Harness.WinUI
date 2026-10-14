// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using Harness.Core.History;

namespace Harness.Core.Memory;

/// <summary>
/// What the memory window draws: the memories and the conversations they came from as nodes, joined by "came from" and
/// "related" lines, plus conversations that look like the one being held ("similar").
/// </summary>
public static class MemoryGraph
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public sealed record MemoryNode(
        string Id, string Text, string Type, string Status, string Scope, int Uses, DateTimeOffset Created, DateTimeOffset Updated,
        DateTimeOffset? LastUsed, string Origin, IReadOnlyList<MemorySource> Sources, IReadOnlyList<string> Related);

    public sealed record SessionNode(string Id, string Title, DateTimeOffset At, bool Current);

    public sealed record Edge(string A, string B, string Kind);

    public sealed record Graph(IReadOnlyList<MemoryNode> Memories, IReadOnlyList<SessionNode> Sessions, IReadOnlyList<Edge> Edges);

    /// <param name="currentConversationId">The conversation on screen; it and the conversations like it are drawn too (null: none).</param>
    public static Graph Build(MemoryStore memories, ConversationStore? history, int archiveDays, string? currentConversationId)
    {
        var items = memories.All();
        var nodes = items.Select(i => new MemoryNode(
            i.Id, i.Text, i.Type.ToString().ToLowerInvariant(),
            MemoryStore.IsArchived(i, archiveDays) ? "archived" : i.Status.ToString().ToLowerInvariant(),
            i.SandboxFolder is null ? "global" : "sandbox", i.Uses, i.Created, i.Updated, i.LastUsed, i.Origin, i.Sources, i.Related)).ToList();

        var edges = new List<Edge>();
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        var known = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var source in item.Sources)
            {
                sessionIds.Add(source.ConversationId);
                edges.Add(new Edge(item.Id, source.ConversationId, "source"));
            }
            foreach (var other in item.Related.Where(known.Contains))
            {
                // Each pair once.
                if (string.CompareOrdinal(item.Id, other) < 0)
                    edges.Add(new Edge(item.Id, other, "related"));
            }
        }

        if (currentConversationId is not null && history is not null)
        {
            sessionIds.Add(currentConversationId);
            foreach (var similar in Similar(history, currentConversationId))
            {
                sessionIds.Add(similar);
                edges.Add(new Edge(currentConversationId, similar, "similar"));
            }
        }

        var sessions = (history?.Describe(sessionIds) ?? [])
            .Select(c => new SessionNode(c.Id, c.Title, c.UpdatedAt, c.Id == currentConversationId)).ToList();
        // A source whose conversation was deleted has nothing to draw.
        var drawn = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        edges = [.. edges.Where(e => (known.Contains(e.A) || drawn.Contains(e.A)) && (known.Contains(e.B) || drawn.Contains(e.B)))];
        return new Graph(nodes, sessions, edges);
    }

    /// <summary>Conversations that mention the longest words of the conversation's first message.</summary>
    private static IEnumerable<string> Similar(ConversationStore history, string conversationId)
    {
        var first = history.Load(conversationId)?.Messages.FirstOrDefault(m => m.Role == "user")?.Text;
        var words = MemoryStore.Words(first).OrderByDescending(w => w.Length).Take(3).ToList();
        var found = new List<string>();
        foreach (var word in words)
        {
            foreach (var hit in history.Search(word, excludeConversationId: conversationId, limit: 4))
            {
                if (!found.Contains(hit.ConversationId))
                    found.Add(hit.ConversationId);
            }
        }
        return found.Take(6);
    }

    public static string ToJson(Graph graph) => JsonSerializer.Serialize(graph, s_json);
}
