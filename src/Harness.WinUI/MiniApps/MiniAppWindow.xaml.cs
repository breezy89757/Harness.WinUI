// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Harness.Core.MiniApps;

namespace Harness.WinUI.MiniApps;

/// <summary>
/// One installed Mini App in a window of its own. The page is served from the app's own origin
/// (<c>https://{id}.harness.miniapps</c>) with the policy its manifest allows, so two apps never share storage, and the
/// only channel to Harness is <c>window.harness</c> (see <see cref="MiniAppBridge"/>), which checks every request.
/// </summary>
public sealed partial class MiniAppWindow : Microsoft.UI.Xaml.Window
{
    private readonly MiniAppStore _store;
    private readonly MiniAppBridge _bridge;
    private MiniAppInfo _app;
    private bool _loaded;
    private bool _closed;

    public MiniAppWindow(MiniAppStore store, MiniAppInfo app, MiniAppBridge bridge)
    {
        InitializeComponent();
        _store = store;
        _bridge = bridge;
        _app = app;
        Title = app.Name;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Harness.ico"));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1000 * scale), (int)(720 * scale)));
        Closed += (_, _) =>
        {
            _closed = true;
            _bridge.Dispose();
            PageView.Close();
        };
        _ = StartAsync();
    }

    public string AppId => _app.Id;

    /// <summary>Shows a newer version (after an update).</summary>
    public void Reload(MiniAppInfo app)
    {
        _app = app;
        Title = app.Name;
        PageView.CoreWebView2?.Navigate(Url());
    }

    private string Url() => $"https://{_app.Host}/";

    private async Task StartAsync()
    {
        // The app's own browser profile: its storage (localStorage and the like) stays between runs, apart from the main window's
        // and the others', and goes when the app is removed.
        var profile = _store.ProfileFolder(_app.Id);
        Directory.CreateDirectory(profile);
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, new CoreWebView2EnvironmentOptions());
        await PageView.EnsureCoreWebView2Async(environment);

        var core = PageView.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true; // the bridge; every request is checked in MiniAppBridge
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;

        core.AddWebResourceRequestedFilter(
            $"https://{_app.Host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;

        // The page stays on its own origin; a link elsewhere is for the person's browser, and only when they clicked it.
        core.NavigationStarting += (_, e) =>
        {
            if (!_loaded || IsOwn(e.Uri))
                return;
            e.Cancel = true;
            if (e.IsUserInitiated)
                ShellLauncher.OpenLink(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated)
                ShellLauncher.OpenLink(e.Uri);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
        core.WebMessageReceived += OnMessage;
        core.NavigationCompleted += (_, _) => _loaded = true;
        core.Navigate(Url());
    }

    private bool IsOwn(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps && parsed.Host.Equals(_app.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Serves the current version's page with the policy its manifest allows; nothing else is on this origin.</summary>
    private void OnResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var environment = sender.Environment;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" ||
            _store.ReadVersion(_app.Id, _app.VersionHash) is not { } html)
        {
            args.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            return;
        }

        var csp = MiniAppManifest.Parse(html).Csp();
        args.Response = environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(html)).AsRandomAccessStream(), 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Security-Policy: {csp}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer");
    }

    /// <summary>A request from the page, taken only from this app's own origin.</summary>
    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOwn(e.Source))
            return;
        try
        {
            await _bridge.HandleAsync(e.WebMessageAsJson, json =>
            {
                if (!_closed)
                    sender.PostWebMessageAsJson(json);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // The window closed while a call was running.
        }
    }

    /// <summary>
    /// <c>window.harness</c>: promises over the one message channel. <c>harness.llm.chat({ messages, system?, stream?, maxTokens? }, onDelta?)</c>
    /// resolves with <c>{ text, inputTokens, outputTokens }</c>; <c>harness.open.link(url)</c> opens a web link in the person's browser.
    /// A failure rejects with an <c>Error</c> that has a <c>code</c> (for example PERMISSION_DENIED).
    /// </summary>
    private const string BridgeScript = """
        (() => {
          const pending = new Map();
          let next = 0;
          chrome.webview.addEventListener('message', event => {
            const m = event.data;
            const call = m && pending.get(m.id);
            if (!call) return;
            if (m.delta !== undefined) { if (call.onDelta) { try { call.onDelta(m.delta); } catch (e) { console.error(e); } } return; }
            pending.delete(m.id);
            if (m.error) call.reject(Object.assign(new Error(m.error.message), { code: m.error.code }));
            else call.resolve(m.result);
          });
          const request = (method, params, onDelta) => new Promise((resolve, reject) => {
            const id = ++next;
            pending.set(id, { resolve, reject, onDelta });
            chrome.webview.postMessage({ id, method, params });
          });
          Object.defineProperty(window, 'harness', {
            value: Object.freeze({
              version: 1,
              llm: Object.freeze({ chat: (options, onDelta) => request('llm.chat', options, onDelta) }),
              open: Object.freeze({ link: url => request('open.link', { url }) }),
            }),
          });
        })();
        """;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
