using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Harness.Browser.Tests;

/// <summary>
/// The agent attaches to a browser the app shows in a pane. The app's pane is a WebView2 with a DevTools port; here a headless Edge with a DevTools port
/// stands in for it (the same protocol), so what is checked is the attaching: the agent drives the page it finds there, and the surface is closed after.
/// </summary>
public class BrowserSurfaceTests
{
    private static readonly string[] s_edgePaths =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ];

    private sealed class EdgeSurface : IBrowserSurface
    {
        private Process? _process;
        private string? _profile;
        private string? _endpoint;
        public int Closed { get; private set; }

        /// <summary>How many pages the browser still had when the agent let go of it.</summary>
        public int PagesAfterDetach { get; private set; }

        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            var edge = s_edgePaths.First(File.Exists);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            _profile = Path.Combine(Path.GetTempPath(), "surface-test-" + Guid.NewGuid().ToString("N"));
            _process = Process.Start(new ProcessStartInfo(edge, $"--headless=new --remote-debugging-port={port} --user-data-dir=\"{_profile}\" about:blank") { UseShellExecute = false, CreateNoWindow = true });
            using var http = new HttpClient();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
                    _endpoint = $"http://127.0.0.1:{port}";
                    return _endpoint;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(200, cancellationToken);
                }
            }
            throw new InvalidOperationException("The test browser did not start.");
        }

        public async Task CloseAsync()
        {
            Closed++;
            try
            {
                using var http = new HttpClient();
                var list = await http.GetStringAsync($"{_endpoint}/json/list");
                PagesAfterDetach = System.Text.Json.JsonDocument.Parse(list).RootElement.EnumerateArray().Count(t => t.GetProperty("type").GetString() == "page");
            }
            catch (HttpRequestException)
            {
                PagesAfterDetach = 0; // the browser went away with the agent
            }

            try
            {
                _process?.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }
    }

    [Fact]
    public async Task The_agent_drives_the_page_of_the_surface_and_the_surface_is_closed_after()
    {
        if (!s_edgePaths.Any(File.Exists))
            return;

        var surface = new EdgeSurface();
        string title;
        await using (var session = await BrowserSession.StartAsync(BrowserOptions.None, headless: false, surface))
        {
            await session.Page.SetContentAsync("<title>in the pane</title><button>Go</button>");
            title = await session.Page.TitleAsync();
        }

        Assert.Equal("in the pane", title);
        Assert.Equal(1, surface.Closed);
        Assert.True(surface.PagesAfterDetach >= 1, "letting go of the browser must not close it: the person may keep using it");
    }
}
