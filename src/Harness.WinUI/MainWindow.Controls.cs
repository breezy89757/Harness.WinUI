// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Harness.WinUI;

/// <summary>Read-only mode, the window-wide shortcuts, and "Retry" on a failed reply.</summary>
public sealed partial class MainWindow
{
    #region Read-only mode

    private void ReadOnlyButton_Click(object sender, RoutedEventArgs e) =>
        SetReadOnlyMode(ReadOnlyButton.IsChecked == true);

    /// <summary>
    /// While on, the agent can read and search but anything that changes files or runs commands is refused (see
    /// <see cref="Harness.Core.Tools.ToolPermissionStore.ReadOnlyMode"/>). It isn't remembered across launches, so
    /// the app never starts with a lock the user has forgotten about.
    /// </summary>
    private void SetReadOnlyMode(bool on)
    {
        _permissions.ReadOnlyMode = on;
        ReadOnlyButton.IsChecked = on;
        ToolTipService.SetToolTip(ReadOnlyButton, on ? Strings.ReadOnlyTooltipOn : Strings.ReadOnlyTooltipOff);
        ShowHint(on ? Strings.ReadOnlyTurnedOn : Strings.ReadOnlyTurnedOff, 1500);
        ShowYoloState(); // the two modes exclude each other
    }

    #endregion

    #region YOLO mode

    private void YoloButton_Click(object sender, RoutedEventArgs e) =>
        UiGuard.Run(() => SetYoloModeAsync(YoloButton.IsChecked == true), "YOLO mode");

    /// <summary>
    /// While on, nothing the agent does is asked about (files, commands, even chained ones), except changes to the app's own
    /// settings. Turning it on asks once to be sure; it isn't remembered across launches.
    /// </summary>
    private async Task SetYoloModeAsync(bool on)
    {
        if (on && !await ConfirmYoloAsync())
        {
            ShowYoloState();
            return;
        }

        _permissions.AutoApprove = on;
        if (on)
            ReadOnlyButton.IsChecked = false;
        ShowYoloState();
        ShowHint(on ? Strings.YoloTurnedOn : Strings.YoloTurnedOff, 1800);
    }

    private void YoloBadge_Click(object sender, RoutedEventArgs e) => UiGuard.Run(() => SetYoloModeAsync(false), "YOLO mode");

    private void ReadOnlyBadge_Click(object sender, RoutedEventArgs e) => SetReadOnlyMode(false);

    private void ShowYoloState()
    {
        var on = _permissions.AutoApprove;
        YoloButton.IsChecked = on;
        YoloBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyBadge.Visibility = _permissions.ReadOnlyMode ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(YoloBadge, Strings.YoloBadgeTooltip);
        ToolTipService.SetToolTip(ReadOnlyBadge, Strings.ReadOnlyBadgeTooltip);
        // The window title says it too, so it shows in the taskbar and when switching windows.
        Title = (string.IsNullOrWhiteSpace(_options.AgentName) ? "Harness.WinUI" : _options.AgentName) + (on ? " — YOLO" : string.Empty);
        ToolTipService.SetToolTip(YoloButton, on ? Strings.YoloTooltipOn : Strings.YoloTooltipOff);
        ReadOnlyButton.IsChecked = _permissions.ReadOnlyMode;
        ToolTipService.SetToolTip(ReadOnlyButton, _permissions.ReadOnlyMode ? Strings.ReadOnlyTooltipOn : Strings.ReadOnlyTooltipOff);
    }

    private async Task<bool> ConfirmYoloAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.YoloConfirmTitle,
            Content = new TextBlock { Text = Strings.YoloConfirmBody, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Strings.YoloConfirmTurnOn,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    #endregion

    #region Shortcuts

    /// <summary>Ctrl+N starts a new chat, Ctrl+H opens the history. Pressed while the chat page has focus, the page forwards them (see "shortcut" in the web messages).</summary>
    private void ConfigureShortcuts()
    {
        void Add(VirtualKey key, string name)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                RunShortcut(name);
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.N, "newChat");
        Add(VirtualKey.H, "history");
    }

    private void RunShortcut(string name)
    {
        // Same conditions as the buttons: nothing while a reply is being written or a dialog is open.
        if (ViewModel.IsBusy || IsDialogOpen)
            return;

        switch (name)
        {
            case "newChat" when ViewModel.NewChatCommand.CanExecute(null):
                ViewModel.NewChatCommand.Execute(null);
                break;
            case "history":
                HistoryFlyout.ShowAt(HistoryButton);
                break;
        }
    }

    #endregion

    #region Retry

    public Task SetRetryAsync(string? id, string? label, string? tooltip = null) =>
        ExecuteShellScriptAsync($"setRetry({ToJs(id)}, {ToJs(label)}, {ToJs(tooltip)});");

    #endregion
}
