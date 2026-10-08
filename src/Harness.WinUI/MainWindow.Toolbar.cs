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
    private sealed record ToolbarItem(string Id, ButtonBase Button, string Glyph, Func<string> Label, Action<FrameworkElement> Open, Func<bool> IsEnabled);

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
            new("readOnly", ReadOnlyButton, "", () => WithDetail(Strings.ReadOnlyMode, _permissions.ReadOnlyMode ? Strings.ReadOnlyOnDetail : null),
                _ => SetReadOnlyMode(!_permissions.ReadOnlyMode), Always),
            new("quality", QualityButton, "", () => Strings.ResponseQuality,
                at => QualityFlyout.ShowAt(at), Always),
            new("usage", UsageButton, "", () => WithDetail(Strings.UsageTooltip, ViewModel.ConversationCostText),
                at => UsageButton.Flyout.ShowAt(at), Always),
            new("observability", ObservabilityButton, "\uE9D9", () => Strings.ObservabilityTitle,
                _ => OpenObservability(null), Always),
        ];
        ApplyToolbar(HiddenToolbarButtons());

        // Narrow window (or the artifact panel open): buttons that don't fit move into More until there's room.
        ComposerGrid.SizeChanged += (_, _) => FitToolbar();
        foreach (var item in _toolbarItems)
            item.Button.SizeChanged += (_, e) =>
            {
                // e.g. the usage button grows with the cost text
                if (e.NewSize.Width > 0 && _buttonWidths.GetValueOrDefault(item.Id) != e.NewSize.Width)
                {
                    _buttonWidths[item.Id] = e.NewSize.Width;
                    FitToolbar();
                }
            };
    }

    private HashSet<string> _userHidden = [];
    private readonly HashSet<string> _overflowed = [];
    private readonly Dictionary<string, double> _buttonWidths = [];

    /// <summary>Room the message box keeps before toolbar buttons start moving into More.</summary>
    private const double MinInputWidth = 260;

    /// <summary>Shows the buttons the user chose, left to right, as long as the message box keeps <see cref="MinInputWidth"/>.</summary>
    private void FitToolbar()
    {
        if (ComposerGrid.ActualWidth <= 0)
            return;
        var spacing = ToolbarPanel.Spacing;
        var available = ComposerGrid.ActualWidth - ComposerGrid.Padding.Left - ComposerGrid.Padding.Right
            - ComposerGrid.ColumnSpacing * 2 - Math.Max(SendButton.ActualWidth, StopButton.ActualWidth) - MinInputWidth
            - (MoreButton.ActualWidth > 0 ? MoreButton.ActualWidth : 48);

        _overflowed.Clear();
        var used = 0.0;
        foreach (var item in _toolbarItems.Where(i => !_userHidden.Contains(i.Id)))
        {
            var width = _buttonWidths.GetValueOrDefault(item.Id, 48) + spacing;
            if (_overflowed.Count == 0 && used + width <= available)
                used += width;
            else
                _overflowed.Add(item.Id);
        }
        foreach (var item in _toolbarItems)
        {
            var visible = !_userHidden.Contains(item.Id) && !_overflowed.Contains(item.Id) ? Visibility.Visible : Visibility.Collapsed;
            if (item.Button.Visibility != visible)
                item.Button.Visibility = visible;
        }
    }

    private static string WithDetail(string label, string? detail) =>
        string.IsNullOrEmpty(detail) ? label : $"{label} ({detail})";

    private static HashSet<string> HiddenToolbarButtons() =>
        [.. AppPreferences.Load().HiddenToolbarButtons ?? []];

    private void ApplyToolbar(HashSet<string> hidden)
    {
        _userHidden = hidden;
        FitToolbar();
        if (ComposerGrid.ActualWidth <= 0)
        {
            foreach (var item in _toolbarItems)
                item.Button.Visibility = hidden.Contains(item.Id) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Saves which buttons are hidden, in the toolbar's own order (so the file reads naturally), and applies it.</summary>
    private void SetHiddenToolbarButtons(IEnumerable<string> ids)
    {
        var hidden = ids.ToHashSet();
        var ordered = _toolbarItems.Select(i => i.Id).Where(hidden.Contains).ToList();
        (AppPreferences.Load() with { HiddenToolbarButtons = ordered.Count > 0 ? ordered : null }).Save();
        ApplyToolbar(hidden);
    }

    /// <summary>
    /// A panel with a check box per button: tick or untick as many as you like, then Apply (nothing changes
    /// until then; Cancel or clicking away leaves the toolbar as it was).
    /// </summary>
    private void ShowToolbarCustomizer(FrameworkElement anchor, Windows.Foundation.Point? position = null)
    {
        var hidden = HiddenToolbarButtons();
        var panel = new StackPanel { Spacing = 2, MinWidth = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.ShowOnToolbar,
            Margin = new Thickness(0, 0, 0, 6),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        var boxes = new List<(string Id, CheckBox Box)>();
        foreach (var item in _toolbarItems)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            label.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 16 });
            label.Children.Add(new TextBlock { Text = item.Label(), VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox { Content = label, IsChecked = !hidden.Contains(item.Id) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, item.Label());
            boxes.Add((item.Id, box));
            panel.Children.Add(box);
        }

        var flyout = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        var apply = new Button { Content = Strings.Apply, Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancel = new Button { Content = Strings.Cancel, HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) =>
        {
            SetHiddenToolbarButtons(boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Id));
            flyout.Hide();
        };
        cancel.Click += (_, _) => flyout.Hide();

        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        flyout.Content = panel;
        var options = new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        if (position is { } at)
            options.Position = at;
        flyout.ShowAt(anchor, options);
    }

    // Built on every open so labels (cost, tool count) and enabled states are current.
    private void MoreFlyout_Opening(object sender, object e)
    {
        MoreFlyout.Items.Clear();
        // Buttons the user hid, and those that don't fit right now.
        var hidden = HiddenToolbarButtons();
        foreach (var item in _toolbarItems.Where(i => hidden.Contains(i.Id) || _overflowed.Contains(i.Id)))
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

        var customize = new MenuFlyoutItem { Text = Strings.CustomizeToolbarEllipsis, Icon = new FontIcon { Glyph = "" } };
        customize.Click += (_, _) => DispatcherQueue.TryEnqueue(() => ShowToolbarCustomizer(MoreButton));
        MoreFlyout.Items.Add(customize);

        var about = new MenuFlyoutItem { Text = Strings.AboutMenu, Icon = new FontIcon { Glyph = "" } };
        about.Click += (_, _) => DispatcherQueue.TryEnqueue(ShowAbout);
        MoreFlyout.Items.Add(about);
    }

    private void ToolbarPanel_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ShowToolbarCustomizer(ToolbarPanel, e.TryGetPosition(ToolbarPanel, out var point) ? point : null);
    }
}
