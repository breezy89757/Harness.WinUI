// Harness.WinUI — Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Harness.WinUI.Panes;

/// <summary>
/// The pane that shows the browser the browser agent drives, beside the chat. It holds a WebView2 of its own: a fresh profile for each run (deleted afterwards, so
/// nothing stays signed in), and a DevTools port on 127.0.0.1 that Playwright attaches to. The person can click and type in it too.
/// </summary>
public sealed partial class BrowserPane : UserControl
{
    private const string ProfilePrefix = "Harness.WinUI-browser-pane-";

    private WebView2? _view;
    private string? _profileFolder;

    public BrowserPane()
    {
        InitializeComponent();
    }

    public bool IsOpen => _view is not null;

    /// <summary>The person pressed the close button.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Creates the browser and returns the DevTools address for Playwright.</summary>
    public async Task<string> OpenAsync()
    {
        if (_view is not null)
            throw new InvalidOperationException("The browser pane is already in use.");

        // a profile left behind by an earlier run that did not end cleanly (the app was closed or crashed while the pane was open)
        // (older than half a day, so that another running copy of the app is never touched)
        foreach (var stale in Directory.EnumerateDirectories(Path.GetTempPath(), ProfilePrefix + "*").Where(f => DateTime.UtcNow - Directory.GetCreationTimeUtc(f) > TimeSpan.FromHours(12)))
            _ = Task.Run(() => DeleteProfileAsync(stale, attempts: 1));

        var port = FreePort();
        _profileFolder =Path.Combine(Path.GetTempPath(), ProfilePrefix + Guid.NewGuid().ToString("N"));
        var view = new WebView2();
        view.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "BrowserPaneView");
        _view = view;
        Host.Children.Add(view);
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, _profileFolder, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={port}" });
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            // a link that wants a new window opens here instead: the agent drives one page
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.SourceChanged += (_, _) => AddressText.Text = core.Source;
            core.Navigate("about:blank");
        }
        catch
        {
            Close();
            throw;
        }
        return $"http://127.0.0.1:{port}";
    }

    public void Close()
    {
        var view = _view;
        var profile = _profileFolder;
        _view = null;
        _profileFolder = null;
        AddressText.Text = string.Empty;
        if (view is not null)
        {
            Host.Children.Remove(view);
            try
            {
                view.Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // already closed
            }
        }

        if (profile is not null)
            _ = Task.Run(() => DeleteProfileAsync(profile));
    }

    /// <summary>The browser process lets go of its folder a moment after the control closes.</summary>
    private static async Task DeleteProfileAsync(string folder, int attempts = 10)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still in use; try again
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
