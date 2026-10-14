// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Harness.Core.Config;
using Harness.Core.History;
using Harness.Core.Memory;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>The memory button and window (one at a time).</summary>
public sealed partial class MainWindow
{
    private MemoryWindow? _memory;

    private void MemoryButton_Click(object sender, RoutedEventArgs e) => OpenMemory();

    /// <param name="showIds">Memories to point out (those a reply was given), or none.</param>
    private void OpenMemory(IReadOnlyList<string>? showIds = null)
    {
        if (!AppPreferences.Load().MemoryEnabled)
        {
            _ = ShowNoticeAsync(Strings.MemoryTurnedOff);
            return;
        }

        if (_memory is null)
        {
            ConversationStore? history = null;
            try
            {
                history = new ConversationStore();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // No history to look into: the notes are still there.
            }

            _memory = new MemoryWindow(
                _tools.Memory, history, () => ViewModel.CurrentConversationId, () => _tools.SandboxRoot, MemoryModel,
                (id, seq) => DispatcherQueue.TryEnqueue(async () =>
                {
                    Activate();
                    await OpenConversationLinkAsync(HistorySearchTool.Link(id, seq));
                }));
            _memory.Closed += (_, _) => _memory = null;
            Closed += (_, _) => _memory?.Close();
        }
        if (_memory.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _memory.Activate();
        if (showIds is { Count: > 0 })
            _memory.Focus(showIds);
    }

    /// <summary>The model that consolidates the notes: the configured one, not recorded.</summary>
    private (IChatClient? Client, string? Model) MemoryModel() =>
        ProviderResolver.TryResolve(_options.Provider) is { } provider
            ? (Harness.Core.Providers.ChatClientFactory.Create(provider, instrument: false), provider.Model)
            : (null, null);

    /// <summary>Turns the memory on or off after Settings changed it: the agent's tools, the button, and the window.</summary>
    private void ApplyMemorySetting()
    {
        var enabled = AppPreferences.Load().MemoryEnabled;
        _tools.SetMemory(enabled);
        ApplyToolbar(HiddenToolbarButtons());
        if (!enabled)
            _memory?.Close();
    }

    /// <summary>Says in the button's tooltip how many new notes wait to be looked over.</summary>
    private void RefreshMemoryTooltip()
    {
        if (!AppPreferences.Load().MemoryEnabled)
            return;
        try
        {
            var newNotes = _tools.Memory.All().Count(i => i.Status == MemoryStatus.New);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MemoryButton, Strings.MemoryTooltip(newNotes));
        }
        catch (IOException)
        {
            // The folder can't be read right now; the tooltip stays as it was.
        }
    }
}
