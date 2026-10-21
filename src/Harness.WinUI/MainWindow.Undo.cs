// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness.WinUI;

/// <summary>"Undo": put back what the agent's file tools changed during one reply (see <see cref="Harness.Core.Files.FileCheckpoints"/>).</summary>
public sealed partial class MainWindow
{
    private async Task UndoTurnAsync(string turnId)
    {
        var files = _tools.Checkpoints.ChangedFiles(turnId);
        if (files.Count == 0 || IsDialogOpen)
            return;

        var root = _tools.SandboxRoot;
        var names = files.Take(12).Select(f => root is not null && f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, f) : f).ToList();
        var list = string.Join("\n", names.Select(n => "• " + n)) + (files.Count > names.Count ? $"\n… +{files.Count - names.Count}" : string.Empty);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.UndoConfirmTitle,
            Content = new TextBlock { Text = Strings.UndoConfirmBody(list), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = Strings.UndoConfirmButton,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var problems = await Task.Run(() => _tools.Checkpoints.Undo(turnId));
        await ExecuteShellScriptAsync($"setMessageUndone({ToJs(turnId)}, {ToJs(problems.Count == 0 ? Strings.UndoDone : Strings.UndoPartly(problems.Count))});");
        ShowHint(problems.Count == 0 ? Strings.UndoDone : Strings.UndoPartly(problems.Count), 2500);
    }
}
