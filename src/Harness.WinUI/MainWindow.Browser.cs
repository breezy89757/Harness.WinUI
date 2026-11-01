// Harness.WinUI — Licensed under the MIT License.

using Harness.Browser;
using Harness.Core.Config;
using Microsoft.UI.Xaml;

namespace Harness.WinUI;

/// <summary>The browser agent (operate_browser): offered to the main agent only while the setting is on. Its browser is shown in the pane beside the chat.</summary>
public sealed partial class MainWindow
{
    private BrowserPaneSurface? _browserSurface;
    private bool _browserPaneHidArtifact;
    private bool _browserRunning;

    private void ApplyBrowserSetting()
    {
        _browserSurface ??= new BrowserPaneSurface(this);
        var surface = _browserSurface;
        _tools.SetExtraTools("browser", AppPreferences.Load().BrowserControl ? context => [BrowserTool.Create(context, surface: surface)] : null);
    }

    /// <summary>
    /// Takes the side column for the browser: where an open artifact is, that one steps aside. The pane stays after a run, with its last page, until the person
    /// closes it or another run replaces it with a fresh browser.
    /// </summary>
    private async Task<string> ShowBrowserPaneAsync()
    {
        if (_browserRunning)
            throw new InvalidOperationException("The browser pane is already in use.");

        if (BrowserPane.Visibility != Visibility.Visible)
        {
            _browserPaneHidArtifact = ArtifactPanel.Visibility == Visibility.Visible;
            if (_browserPaneHidArtifact)
                ArtifactPanel.Visibility = Visibility.Collapsed;
            else
                OpenSideColumn();
            BrowserPane.Visibility = Visibility.Visible;
        }
        else
        {
            BrowserPane.Close(); // the page of an earlier run
        }

        _browserRunning = true;
        try
        {
            return await BrowserPane.OpenAsync();
        }
        catch
        {
            HideBrowserPane();
            throw;
        }
    }

    /// <summary>Closes the browser and gives the column back (to the artifact that stepped aside, if there was one).</summary>
    private void HideBrowserPane()
    {
        _browserRunning = false;
        BrowserPane.Close();
        if (BrowserPane.Visibility != Visibility.Visible)
            return;
        BrowserPane.Visibility = Visibility.Collapsed;
        if (_browserPaneHidArtifact)
            ArtifactPanel.Visibility = Visibility.Visible;
        else
            CloseSideColumn();
        _browserPaneHidArtifact = false;
    }

    private void BrowserPane_CloseRequested(object? sender, EventArgs e) => HideBrowserPane();

    /// <summary>What the browser agent sees of the pane: open it, get the DevTools address; close it when the run ends.</summary>
    private sealed class BrowserPaneSurface(MainWindow window) : IBrowserSurface
    {
        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            var address = new TaskCompletionSource<string>();
            await window.RunOnUIThreadAsync(async () =>
            {
                try
                {
                    address.SetResult(await window.ShowBrowserPaneAsync());
                }
                catch (Exception ex)
                {
                    // whatever went wrong, the caller goes on with a separate browser window
                    address.SetException(new InvalidOperationException(ex.Message, ex));
                }
            });
            return await address.Task;
        }

        // The run is over; the browser stays on screen for the person to look at (or use) until they close it.
        public Task CloseAsync() => window.RunOnUIThreadAsync(() =>
        {
            window._browserRunning = false;
            return Task.CompletedTask;
        });
    }
}
