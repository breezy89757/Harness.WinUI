// Harness.WinUI — Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Harness.Core.Config;
using Harness.Core.Skills;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>
/// Typing "/" at the start of a message lists the enabled skills, filtered as you type. Picking one
/// (arrow keys then Enter or Tab, or a click) fills in "/skill-name " and you add your request; when
/// sent, the model is told to use that skill (see <see cref="SkillCatalog.ExpandInvocation"/>).
/// </summary>
public sealed partial class MainWindow
{
    // The whole message so far is just a slash and the start of a skill name.
    [GeneratedRegex("^/[A-Za-z0-9-]*$")]
    private static partial Regex SlashQuery();

    private IReadOnlyList<Skill> EnabledSkills()
    {
        var disabled = AppPreferences.Load().DisabledSkills;
        return disabled is null ? _tools.Skills : _tools.Skills.Where(s => !disabled.Contains(s.Name)).ToList();
    }

    private bool SkillSuggestionsOpen => SkillSuggestions.Visibility == Visibility.Visible;

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSkillSuggestions();

    private void UpdateSkillSuggestions()
    {
        var text = InputTextBox.Text;
        if (!SlashQuery().IsMatch(text))
        {
            HideSkillSuggestions();
            return;
        }

        var query = text[1..];
        var matches = EnabledSkills()
            .Select(s => (Skill: s, Rank: Rank(s, query)))
            .Where(m => m.Rank < 3)
            .OrderBy(m => m.Rank).ThenBy(m => m.Skill.Name, StringComparer.Ordinal)
            .Select(m => new SkillSuggestion(m.Skill.Name, m.Skill.Description.ReplaceLineEndings(" ")))
            .ToList();
        if (matches.Count == 0)
        {
            HideSkillSuggestions();
            return;
        }

        SkillList.ItemsSource = matches;
        SkillList.SelectedIndex = 0;
        SkillSuggestions.Visibility = Visibility.Visible;
    }

    /// <summary>0: the name starts with what was typed, 1: contains it, 2: the description does, 3: no match.</summary>
    private static int Rank(Skill skill, string query)
    {
        if (query.Length == 0 || skill.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private void HideSkillSuggestions()
    {
        if (SkillSuggestionsOpen)
            SkillSuggestions.Visibility = Visibility.Collapsed;
    }

    /// <summary>Keys while the list is open. True when the key was used for it and must not reach the message box.</summary>
    private bool HandleSkillSuggestionKey(VirtualKey key)
    {
        if (!SkillSuggestionsOpen || _isComposing)
            return false;

        switch (key)
        {
            case VirtualKey.Down:
            case VirtualKey.Up:
                var count = SkillList.Items.Count;
                var next = SkillList.SelectedIndex + (key == VirtualKey.Down ? 1 : -1);
                SkillList.SelectedIndex = (next + count) % count;
                SkillList.ScrollIntoView(SkillList.SelectedItem);
                return true;
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                if (SkillList.SelectedItem is SkillSuggestion chosen)
                    AcceptSkill(chosen);
                return true;
            case VirtualKey.Escape:
                HideSkillSuggestions();
                return true;
            default:
                return false;
        }
    }

    private void SkillList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SkillSuggestion chosen)
            AcceptSkill(chosen);
    }

    private void AcceptSkill(SkillSuggestion skill)
    {
        InputTextBox.Text = skill.Label + " ";
        InputTextBox.SelectionStart = InputTextBox.Text.Length;
        HideSkillSuggestions();
        InputTextBox.Focus(FocusState.Programmatic);
    }
}
