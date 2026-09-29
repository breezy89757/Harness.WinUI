// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI.ViewModels;

/// <summary>A saved conversation as listed in the History flyout.</summary>
/// <param name="When">Localized relative time of the last message, e.g. "5 分鐘前".</param>
/// <param name="IsCurrent">The conversation currently shown in the chat.</param>
public sealed record ConversationItem(string Id, string Title, string When, bool IsCurrent)
{
    public string DeleteLabel => Strings.DeleteConversation;
}
