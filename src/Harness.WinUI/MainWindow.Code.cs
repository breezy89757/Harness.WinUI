// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Harness.Core.Agent;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>
/// Chat or Code. Code mode is a coding agent in a project folder: its own instructions and tools, the folder chosen from the
/// bar above the conversation (and remembered), its own conversations per folder, and "always allow" choices that belong to the
/// folder. Chat mode is what the app always was.
/// </summary>
public sealed partial class MainWindow
{
    private const int RecentWorkspaceCount = 8;

    private AppMode _mode = AppMode.Chat;
    private string? _workspace;
    private bool _modeUiLoading;

    private void ConfigureMode()
    {
        // /init (Code mode): asks the agent to write AGENTS.md for the project; anything typed after it is added to the request.
        // /explore (Code mode): looks into a question with helpers working at the same time; without a question, an overview of the project.
        ViewModel.BuiltInCommand = message =>
        {
            if (_mode != AppMode.Code)
                return null;
            var text = message.Trim();
            if (CommandArgument(text, CodingPrompt.ExploreCommand) is { } question)
                return CodingPrompt.ExploreRequest(question.Length == 0 ? Strings.ExploreOverview : question);
            if (CommandArgument(text, CodingPrompt.InitCommand) is not { } extra)
                return null;
            _tools.AllowInstructionsWrite = true; // this one message may write AGENTS.md; see TurnFinished below
            return extra.Length == 0 ? CodingPrompt.InitRequest : CodingPrompt.InitRequest + "\n\nAlso: " + extra;
        };

        ViewModel.TurnFinished += (_, _) => _tools.AllowInstructionsWrite = false;

        var preferences = AppPreferences.Load();
        _workspace = preferences.CodeWorkspace is { } saved && Directory.Exists(saved) ? saved : null;

        // Start in the mode last used; Code mode needs a folder that is still there.
        var mode = preferences.Mode == "code" && _workspace is not null ? AppMode.Code : AppMode.Chat;
        _modeUiLoading = true;
        ApplyMode(mode);
        _modeUiLoading = false;
    }

    /// <summary>What follows "/name" when the message is that command (empty for the bare command); null when it is something else.</summary>
    private static string? CommandArgument(string text, string name)
    {
        var command = "/" + name;
        if (text.Equals(command, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase) ? text[command.Length..].Trim() : null;
    }

    /// <summary>Makes the tools, the sandbox, the permissions scope and the bar match <paramref name="mode"/> (and the current folder).</summary>
    private void ApplyMode(AppMode mode)
    {
        _mode = mode;
        _tools.SetMode(mode);

        var code = mode == AppMode.Code;
        _tools.SetSandbox(code ? _workspace : AppPreferences.Load().SandboxFolder);
        _permissions.Scope = code ? _workspace : null;

        ViewModel.ConversationMode = code ? "code" : "chat";
        ViewModel.ConversationWorkspace = code ? _workspace : null;

        ModeSelector.SelectedItem = code ? CodeModeItem : ChatModeItem;
        WorkspaceButton.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceText.Text = _workspace is null ? Strings.WorkspaceNone : Path.GetFileName(_workspace.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : _workspace;
        ToolTipService.SetToolTip(WorkspaceButton, _workspace ?? Strings.WorkspaceNone);
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        UiGuard.Run(() => ChangeModeAsync(sender), "switch mode");

    private async Task ChangeModeAsync(SelectorBar sender)
    {
        if (_modeUiLoading)
            return;

        var wanted = sender.SelectedItem?.Tag as string == "code" ? AppMode.Code : AppMode.Chat;
        if (wanted == _mode)
            return;

        // A reply being written belongs to the conversation on screen; don't switch under it.
        if (ViewModel.IsBusy)
        {
            ShowHint(Strings.ModeBusy, 2000);
            RestoreModeSelection();
            return;
        }

        if (wanted == AppMode.Code && _workspace is null && !await PickWorkspaceAsync(announce: false))
        {
            RestoreModeSelection();
            return;
        }

        ApplyMode(wanted);
        (AppPreferences.Load() with { Mode = wanted == AppMode.Code ? "code" : "chat" }).Save();
        await ViewModel.StartFreshConversationAsync();
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private void RestoreModeSelection()
    {
        _modeUiLoading = true;
        ModeSelector.SelectedItem = _mode == AppMode.Code ? CodeModeItem : ChatModeItem;
        _modeUiLoading = false;
    }

    private void WorkspaceFlyout_Opening(object sender, object e)
    {
        WorkspaceFlyout.Items.Clear();
        foreach (var folder in (AppPreferences.Load().RecentWorkspaces ?? []).Where(Directory.Exists).Take(RecentWorkspaceCount))
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = folder,
                IsChecked = string.Equals(folder, _workspace, StringComparison.OrdinalIgnoreCase),
                Icon = new FontIcon { Glyph = "" },
            };
            item.Click += (_, _) => UiGuard.Run(() => SetWorkspaceAsync(folder), "choose folder");
            WorkspaceFlyout.Items.Add(item);
        }
        if (WorkspaceFlyout.Items.Count > 0)
            WorkspaceFlyout.Items.Add(new MenuFlyoutSeparator());

        var pick = new MenuFlyoutItem { Text = Strings.WorkspacePick, Icon = new FontIcon { Glyph = "" } };
        pick.Click += (_, _) => UiGuard.Run(() => PickWorkspaceAsync(announce: true), "pick folder");
        WorkspaceFlyout.Items.Add(pick);

        if (_workspace is not null)
        {
            var reveal = new MenuFlyoutItem { Text = Strings.WorkspaceReveal, Icon = new FontIcon { Glyph = "" } };
            reveal.Click += (_, _) => ShellLauncher.OpenLink(_workspace);
            WorkspaceFlyout.Items.Add(reveal);
        }
    }

    private void ToggleCodeMcpServer(string name, bool on, IReadOnlyList<string> all)
    {
        var current = AppPreferences.Load().CodeMcpServers ?? [.. all];
        var next = on ? current.Append(name).Distinct(StringComparer.Ordinal).ToList() : current.Where(n => n != name).ToList();
        (AppPreferences.Load() with { CodeMcpServers = next.Count == all.Count ? null : next }).Save();
        _tools.ApplyMcpFilter();
    }

    /// <summary>Lets the user pick a project folder and switches to it. False if they cancelled or it couldn't be switched to.</summary>
    private async Task<bool> PickWorkspaceAsync(bool announce)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return false;
        return await SetWorkspaceAsync(folder.Path, announce);
    }

    /// <summary>Makes <paramref name="folder"/> the project Code mode works in, and starts a fresh conversation about it.</summary>
    private async Task<bool> SetWorkspaceAsync(string folder, bool announce = true)
    {
        if (!Directory.Exists(folder))
            return false;
        if (ViewModel.IsBusy)
        {
            ShowHint(Strings.ModeBusy, 2000);
            return false;
        }

        var preferences = AppPreferences.Load();
        List<string> recent = [folder, .. (preferences.RecentWorkspaces ?? []).Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))];
        (preferences with { CodeWorkspace = folder, RecentWorkspaces = [.. recent.Take(RecentWorkspaceCount * 2)] }).Save();
        _workspace = folder;

        if (_mode == AppMode.Code)
        {
            ApplyMode(AppMode.Code);
            if (announce)
                await ViewModel.StartFreshConversationAsync();
        }
        return true;
    }
}
