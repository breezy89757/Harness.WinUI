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
