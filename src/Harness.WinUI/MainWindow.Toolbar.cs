// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>
/// The composer toolbar is the user's to arrange: right-click it (or More › Customize toolbar) to pick
/// which buttons show. Hidden buttons stay one click away in the More menu, so nothing is lost.
/// </summary>
public sealed partial class MainWindow
{
    /// <param name="Open">Runs the button's action; flyouts open at the given element (the More button).</param>
    private sealed record ToolbarItem(string Id, Button Button, string Glyph, Func<string> Label, Action<FrameworkElement> Open, Func<bool> IsEnabled);

    private ToolbarItem[] _toolbarItems = [];

    private void ConfigureToolbar()
    {
        static bool Always() => true;
        _toolbarItems =
        [
            new("newChat", NewChatButton, "", () => Strings.NewChat,
                _ => ViewModel.NewChatCommand.Execute(null), () => ViewModel.NewChatCommand.CanExecute(null)),
            new("history", HistoryButton, "", () => Strings.History,
                at => HistoryFlyout.ShowAt(at), () => !ViewModel.IsBusy),
            new("settings", SettingsButton, "", () => Strings.SettingsTooltip,
                _ => SettingsButton_Click(MoreButton, new RoutedEventArgs()), Always),
            new("tools", ToolsButton, "", () => WithDetail(Strings.ToolsTooltip, ViewModel.McpBadgeText),
                _ => ToolsButton_Click(MoreButton, new RoutedEventArgs()), Always),
            new("quality", QualityButton, "", () => Strings.ResponseQuality,
                at => QualityFlyout.ShowAt(at), Always),
            new("usage", UsageButton, "", () => WithDetail(Strings.UsageTooltip, ViewModel.ConversationCostText),
                at => UsageButton.Flyout.ShowAt(at), Always),
        ];
        ApplyToolbar(HiddenToolbarButtons());
    }

    private static string WithDetail(string label, string? detail) =>
        string.IsNullOrEmpty(detail) ? label : $"{label} ({detail})";

    private static HashSet<string> HiddenToolbarButtons() =>
        [.. AppPreferences.Load().HiddenToolbarButtons ?? []];

    private void ApplyToolbar(HashSet<string> hidden)
    {
        foreach (var item in _toolbarItems)
            item.Button.Visibility = hidden.Contains(item.Id) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetToolbarButtonShown(string id, bool shown)
    {
        var hidden = HiddenToolbarButtons();
        if (shown)
            hidden.Remove(id);
        else
            hidden.Add(id);
        // Keep the toolbar's own order in the file, so it reads naturally if edited by hand.
        var ordered = _toolbarItems.Select(i => i.Id).Where(hidden.Contains).ToList();
        (AppPreferences.Load() with { HiddenToolbarButtons = ordered.Count > 0 ? ordered : null }).Save();
        ApplyToolbar(hidden);
    }

    /// <summary>One checkable entry per toolbar button.</summary>
    private IEnumerable<MenuFlyoutItemBase> ToolbarToggles()
    {
        var hidden = HiddenToolbarButtons();
        foreach (var item in _toolbarItems)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = item.Label(),
                Icon = new FontIcon { Glyph = item.Glyph },
                IsChecked = !hidden.Contains(item.Id),
            };
            toggle.Click += (_, _) => SetToolbarButtonShown(item.Id, toggle.IsChecked);
            yield return toggle;
        }
    }

    // Built on every open so labels (cost, tool count) and enabled states are current.
    private void MoreFlyout_Opening(object sender, object e)
    {
        MoreFlyout.Items.Clear();
        var hidden = HiddenToolbarButtons();
        foreach (var item in _toolbarItems.Where(i => hidden.Contains(i.Id)))
        {
            var entry = new MenuFlyoutItem
            {
                Text = item.Label(),
                Icon = new FontIcon { Glyph = item.Glyph },
                IsEnabled = item.IsEnabled(),
            };
            // Let the menu finish closing before a flyout opens in its place.
            entry.Click += (_, _) => DispatcherQueue.TryEnqueue(() => item.Open(MoreButton));
            MoreFlyout.Items.Add(entry);
        }
        if (MoreFlyout.Items.Count > 0)
            MoreFlyout.Items.Add(new MenuFlyoutSeparator());

        var customize = new MenuFlyoutSubItem { Text = Strings.CustomizeToolbar, Icon = new FontIcon { Glyph = "" } };
        foreach (var toggle in ToolbarToggles())
            customize.Items.Add(toggle);
        MoreFlyout.Items.Add(customize);
    }

    private void ToolbarPanel_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Strings.ShowOnToolbar, IsEnabled = false });
        foreach (var toggle in ToolbarToggles())
            menu.Items.Add(toggle);

        var options = new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        if (e.TryGetPosition(ToolbarPanel, out var point))
            options.Position = point;
        menu.ShowAt(ToolbarPanel, options);
    }
}
