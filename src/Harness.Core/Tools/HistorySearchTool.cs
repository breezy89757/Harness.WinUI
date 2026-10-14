// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Harness.Core.History;

namespace Harness.Core.Tools;

/// <summary>
/// search_history: finds what was said in the user's past conversations, with a link to each hit that opens that
/// conversation at that message. Off unless the user turns it on in Settings, and every use is asked about,
/// because the results go to the model.
/// </summary>
public static class HistorySearchTool
{
    public const string ServerName = "history";
    public const string Name = "search_history";

    /// <summary>Scheme of the links to a conversation: <c>harness://conversation/{id}#{seq}</c>.</summary>
    public const string LinkScheme = "harness";

    public static string Link(string conversationId, long seq) => $"{LinkScheme}://conversation/{conversationId}#{seq}";

    /// <summary>Reads a link made by <see cref="Link"/>; false for anything else.</summary>
    public static bool TryParseLink(string? href, out string conversationId, out long seq)
    {
        conversationId = string.Empty;
        seq = 0;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, LinkScheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "conversation", StringComparison.OrdinalIgnoreCase))
            return false;

        conversationId = uri.AbsolutePath.Trim('/');
        if (conversationId.Length == 0 || conversationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            return false;
        _ = long.TryParse(uri.Fragment.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out seq);
        return true;
    }

    /// <param name="currentConversationId">The conversation the question is asked in; its own messages are left out of the results.</param>
    public static AITool Create(ConversationStore store, IToolApprover approver, ToolPermissionStore permissions, Func<string?> currentConversationId) =>
        new ApprovalGatedFunction(
            AIFunctionFactory.Create(
                (
                    [Description("Words to look for; every word must appear in the message. Use the words the user would have used then, in their language. Try a different or shorter word if nothing is found.")] string query,
                    [Description("Only messages on or after this day (yyyy-MM-dd).")] string? since = null,
                    [Description("Only messages on or before this day (yyyy-MM-dd).")] string? until = null,
                    [Description("At most this many messages, newest first (default 10, at most 30).")] int limit = 10) =>
                        Search(store, currentConversationId(), query, since, until, limit),
                Name,
                "Search the user's past conversations in Harness.WinUI (what they wrote and what you answered, not tool output) to find when and in which conversation something was discussed or done. " +
                "Each result has a link: when you mention a result, write it as a Markdown link to that exact address, e.g. [conversation title](harness://conversation/…#3), copying it character for character; " +
                "the user can click it to open that conversation at that message. Say when it was (date) and what was done there. If nothing is found, say so; don't guess. " +
                "Not for finding files or information in the sandbox folder."),
            ServerName, Name, readOnly: true, approver, permissions, readsPrivateData: true);

    private static string Search(ConversationStore store, string? exclude, string query, string? since, string? until, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: give at least one word to look for.";
        if (!TryDay(since, out var from) || !TryDay(until, out var to))
            return "Error: dates must look like 2026-10-11.";

        // "until" is a whole day: up to the start of the next one.
        var hits = store.Search(query, from, to?.AddDays(1), exclude, limit);
        var output = new StringBuilder();
        output.Append("Today is ").Append(DateTime.Now.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture)).AppendLine(".");
        if (hits.Count == 0)
        {
            output.Append("No messages found for: ").Append(query);
            return output.ToString();
        }

        output.Append(hits.Count).AppendLine(" messages found, newest first:");
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            output.AppendLine()
                .Append(i + 1).Append(". ").Append(hit.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(" · conversation \"").Append(hit.Title).Append("\" · ").AppendLine(hit.Role == "user" ? "the user wrote:" : "you answered:")
                .Append("   ").AppendLine(hit.Snippet)
                .Append("   Link: ").AppendLine(Link(hit.ConversationId, hit.Seq));
        }
        return output.ToString();
    }

    private static bool TryDay(string? text, out DateTimeOffset? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        return true;
    }
}
