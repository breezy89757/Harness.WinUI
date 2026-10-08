// Harness.WinUI — Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Harness.Core.Agent;
using Harness.Core.Tools;
using Harness.MarkdownRendering;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>
/// MCP Apps in the chat: a tool that declares a <c>ui://</c> view gets it rendered under its step, in
/// an iframe served from its own origin (<c>https://{viewId}.harness.apps</c>) with the CSP the view
/// declared. The chat page relays the view's postMessage JSON-RPC here; <see cref="McpAppSession"/>
/// answers it.
/// </summary>
public sealed partial class MainWindow : IMcpAppHost
{
    private readonly Dictionary<string, (McpAppSession Session, McpAppResource Resource)> _appViews = new(StringComparer.Ordinal);
    private string _theme = "light";

    private void ConfigureMcpApps(CoreWebView2 core)
    {
        _theme = ThemeName();
        ViewModel.AppContextProvider = TakeAppModelContext;
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += async (_, _) =>
            {
                _theme = ThemeName();
                foreach (var (session, _) in _appViews.Values.ToList())
                    await session.NotifyThemeChangedAsync();
            };
        }

        core.AddWebResourceRequestedFilter(
            $"https://*.{ChatShell.AppsHostSuffix}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnAppResourceRequested;

        // A view may navigate only within its own origin; anything else is a link for the user's browser.
        core.FrameNavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsAppOrigin(uri))
                return;
            e.Cancel = true;
            if (e.IsUserInitiated)
                ShellLauncher.OpenLink(e.Uri);
        };
    }

    private string ThemeName() =>
        (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark ? "dark" : "light";

    private static bool IsAppOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith("." + ChatShell.AppsHostSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Serves each view's HTML from its own origin, with the view's Content-Security-Policy.</summary>
    private void OnAppResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !IsAppOrigin(uri))
            return;

        var viewId = uri.Host[..^(ChatShell.AppsHostSuffix.Length + 1)];
        var environment = ChatWebView.CoreWebView2.Environment;
        if (uri.AbsolutePath != "/" || !_appViews.TryGetValue(viewId, out var view))
        {
            args.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            return;
        }

        var body = new MemoryStream(Encoding.UTF8.GetBytes(view.Resource.Html)).AsRandomAccessStream();
        args.Response = environment.CreateWebResourceResponse(body, 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Security-Policy: {view.Resource.Csp}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer");
    }

    public async Task PresentToolAppAsync(string id, ToolCallStarted call, object? result)
    {
        // The view gets the full result (the model only saw its content); see McpHost.TakeAppResult.
        var full = _tools.Mcp.TakeAppResult(call.CallId) ?? result as JsonElement?;
        if (_tools.Mcp.FindAppTool(call.Name) is not { } tool || full is not { } toolResult)
            return;

        try
        {
            var resource = await Task.Run(() => _tools.Mcp.ReadAppResourceAsync(tool));
            if (resource is null)
                return;

            await RunOnUIThreadAsync(async () =>
            {
                var viewId = Guid.NewGuid().ToString("N")[..16];
                var session = new McpAppSession(viewId, _tools.Mcp, this, tool, call.CallId, call.Arguments, toolResult);
                _appViews[viewId] = (session, resource);
                await ExecuteShellScriptAsync(
                    $"appendAppView({ToJs(id)}, {ToJs(viewId)}, {ToJs($"https://{viewId}.{ChatShell.AppsHostSuffix}")}, {(resource.PrefersBorder ? "true" : "false")});");
            });
        }
        catch (Exception ex)
        {
            await UpsertMessageStepAsync(id, "app-" + call.CallId, System.Net.WebUtility.HtmlEncode(Strings.AppUnavailable(call.Name, ex.Message)), StepState.Error);
        }
    }

    /// <summary>What the views reported with ui/update-model-context since the last message, labelled for the model.</summary>
    private string? TakeAppModelContext()
    {
        var parts = _appViews.Values
            .Select(v => (v.Session.ServerName, Context: v.Session.TakeModelContext()))
            .Where(p => p.Context is not null)
            .Select(p => $"[Context from the {p.ServerName} app shown in the chat (reported by the app, not typed by the user):\n{p.Context}]")
            .ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>A message the chat page relayed from a view.</summary>
    private void OnAppMessage(string viewId, JsonElement message)
    {
        if (_appViews.TryGetValue(viewId, out var view))
            _ = view.Session.HandleAsync(message);
    }

    private async Task TeardownAppViewsAsync()
    {
        foreach (var (session, _) in _appViews.Values.ToList())
            await session.TeardownAsync("conversation cleared");
        _appViews.Clear();
    }

    #region IMcpAppHost

    public string Theme => _theme;

    public Task PostToViewAsync(string viewId, string json) =>
        ExecuteShellScriptAsync($"deliverToApp({ToJs(viewId)}, {json});");

    public Task<bool> SendUserMessageAsync(string text)
    {
        var sent = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(() => sent.SetResult(ViewModel.TrySendFromApp(text)));
        return sent.Task;
    }

    public void OpenLink(string url) => DispatcherQueue.TryEnqueue(() => ShellLauncher.OpenLink(url));

    public void SetViewHeight(string viewId, double height) =>
        _ = ExecuteShellScriptAsync(
            $"setAppHeight({ToJs(viewId)}, {Math.Ceiling(height).ToString(System.Globalization.CultureInfo.InvariantCulture)});");

    public Task<bool> ApproveToolCallAsync(string serverName, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        // Read-only mode covers tools a view calls too, not only the agent's own.
        if (_permissions.ReadOnlyMode)
            return Task.FromResult(false);

        var decided = new TaskCompletionSource<bool>();
        _attention.Notify(Strings.NotifyApprovalTitle, Strings.NotifyApprovalBody(toolName));
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = Strings.AppToolApprovalTitle(serverName),
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = Strings.AppToolApprovalBody(toolName), TextWrapping = TextWrapping.Wrap },
                            new TextBlock
                            {
                                Text = JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true }),
                                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                                TextWrapping = TextWrapping.Wrap,
                                IsTextSelectionEnabled = true,
                            },
                        },
                    },
                    PrimaryButtonText = Strings.AllowOnce,
                    CloseButtonText = Strings.Deny,
                    DefaultButton = ContentDialogButton.Close,
                };
                decided.SetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception)
            {
                decided.TrySetResult(false); // e.g. another dialog is already open
            }
        });
        return decided.Task;
    }

    #endregion
}
