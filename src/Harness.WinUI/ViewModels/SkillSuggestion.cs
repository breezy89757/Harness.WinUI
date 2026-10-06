// Harness.WinUI — Licensed under the MIT License.

namespace Harness.WinUI.ViewModels;

/// <summary>A skill offered in the list that opens when a message starts with a slash.</summary>
public sealed record SkillSuggestion(string Name, string Description)
{
    public string Label => "/" + Name;
}
