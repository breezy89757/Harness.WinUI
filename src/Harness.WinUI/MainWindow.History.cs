// Harness.WinUI — Licensed under the MIT License.

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>What the History flyout does besides opening and deleting: search, rename and export; plus copying text out of the chat.</summary>
public sealed partial class MainWindow
{
    private int _historySearchVersion;

    private async Task ReloadHistoryAsync()
    {
        ViewModel.HistorySearch = HistorySearchBox.Text;
        await ViewModel.RefreshHistoryAsync();
        UpdateHistoryEmptyText();
    }

    private void UpdateHistoryEmptyText()
    {
        HistoryEmptyText.Text = string.IsNullOrWhiteSpace(HistorySearchBox.Text) ? Strings.NoHistory : Strings.NoHistoryMatches;
        HistoryEmptyText.Visibility = ViewModel.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Search once the typing pauses, not on every key.
        var version = ++_historySearchVersion;
        await Task.Delay(200);
        if (version == _historySearchVersion)
            await ReloadHistoryAsync();
    }

    private async void RenameConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConversationItem item } || IsDialogOpen)
            return;

        HistoryFlyout.Hide();
        var box = new TextBox
        {
            Text = item.Title,
            MaxLength = ChatViewModel.MaxTitleLength,
        };
        AutomationProperties.SetAutomationId(box, "RenameConversationBox");
        AutomationProperties.SetName(box, Strings.RenameConversationTitle);
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.RenameConversationTitle,
            Content = box,
            PrimaryButtonText = Strings.Save,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        // Saving an empty title would leave an unnamed row in the list.
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await ViewModel.RenameConversationAsync(item.Id, box.Text);
            }
            catch (SqliteException ex)
            {
                ShowHint(Strings.SaveFileFailed(ex.Message), 3500);
            }
        }
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async void ExportConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConversationItem item })
            return;

        HistoryFlyout.Hide();
        try
        {
            if (await ViewModel.ExportConversationAsync(item.Id) is not var (title, markdown))
                return;

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = SafeFileName(title),
            };
            picker.FileTypeChoices.Add("Markdown", [".md"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            await File.WriteAllTextAsync(file.Path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ShowHint(Strings.FileSaved(file.Name), 2500);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException)
        {
            ShowHint(Strings.SaveFileFailed(ex.Message), 3500);
        }
    }

    /// <summary>A title as a file name: no characters Windows forbids, and not too long.</summary>
    private static string SafeFileName(string title)
    {
        var cleaned = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (cleaned.Length > 60)
            cleaned = cleaned[..60].TrimEnd();
        return cleaned.Length > 0 ? cleaned : "conversation";
    }

    /// <summary>The chat page's copy buttons hand the text over; the page can't reliably write to the clipboard itself.</summary>
    private void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowHint(Strings.Copied);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The clipboard is held by another app right now; the user can press copy again.
        }
    }
}
