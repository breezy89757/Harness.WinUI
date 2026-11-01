// Harness.WinUI — Licensed under the MIT License.

namespace Harness.Browser;

/// <summary>
/// A place the host app shows the browser the agent drives (a pane beside the chat), instead of a separate browser window. The host opens a
/// Chromium-based view that accepts a DevTools connection and gives its address; Playwright attaches to it. It is closed when the run ends.
/// </summary>
public interface IBrowserSurface
{
    /// <summary>Shows the browser and returns the DevTools address to attach to (for example http://127.0.0.1:9333).</summary>
    Task<string> OpenAsync(CancellationToken cancellationToken);

    /// <summary>The run is over (the agent has let go of the browser). The host may keep showing the browser for the person to look at.</summary>
    Task CloseAsync();
}
