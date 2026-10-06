// Harness.WinUI — Licensed under the MIT License.

using System.Text;

namespace Harness.Core.History;

/// <summary>A saved conversation as a readable Markdown document, for sharing or keeping outside the app.</summary>
public static class ConversationMarkdown
{
    /// <param name="userLabel">Heading for the user's messages, e.g. "You".</param>
    /// <param name="assistantLabel">Heading for the model's replies.</param>
    /// <param name="toolLabel">Prefix of the line listing the tools a reply used, e.g. "Tools used".</param>
    public static string Build(StoredConversation conversation, string userLabel, string assistantLabel, string toolLabel)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(conversation.Title)).Append("\n\n");

        foreach (var message in conversation.Messages)
        {
            // System notes (e.g. "the context was lost") aren't part of the conversation.
            if (message.Role is not ("user" or "assistant"))
                continue;

            sb.Append("## ").Append(message.Role == "user" ? userLabel : assistantLabel).Append("\n\n");
            if (message.Steps is { Count: > 0 } steps)
            {
                var names = steps.Select(s => s.Name).Distinct(StringComparer.Ordinal);
                sb.Append('*').Append(toolLabel).Append(": ").Append(string.Join(", ", names)).Append("*\n\n");
            }

            sb.Append(message.Text.Trim()).Append("\n\n");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
