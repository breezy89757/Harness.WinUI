// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Harness.WinUI.ViewModels;
using Harness.Core.Agent;
using Harness.Core.Artifacts;
using Harness.Core.Config;
using Harness.Core.History;
using Harness.Core.Providers;
using Harness.Core.Tools;
using Harness.MarkdownRendering;

namespace Harness.WinUI;

/// <summary>
/// Hosts the chat WebView, the composer and the artifact panel, and implements
/// <see cref="IChatMessageSink"/> for the ViewModel. Dialog flows (settings, MCP) are coordinated here;
/// the artifact panel's own behavior lives in <see cref="Artifacts.ArtifactPanel"/>.
/// </summary>
public sealed partial class MainWindow : Window, IChatMessageSink
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public ChatViewModel ViewModel { get; }

    private readonly HarnessOptions _options;
    private readonly bool _needsProviderSetup;
    private readonly AgentTools _tools;
    private readonly ToolPermissionStore _permissions = new();
    private bool _shellLoaded;
    private bool _isComposing;

    public MainWindow(ChatSession? chatSession, HarnessOptions options, string? startupError, bool needsProviderSetup, AgentTools tools)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(options.AgentName) ? "Harness.WinUI" : options.AgentName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Harness.ico"));
        _options = options;
        _needsProviderSetup = needsProviderSetup;
        _tools = tools;
        ViewModel = new ChatViewModel(chatSession, this, OpenHistory(), startupError);
        ViewModel.SkillsProvider = EnabledSkills;
        ViewModel.Permissions = _permissions;
        ArtifactPanel.WindowHandle = WindowHandle;

        // The ViewModel is the approver for every gated tool call.
        _tools.Configure(ViewModel, _permissions);
        _tools.Mcp.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshToolStatus);
        _tools.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshToolStatus);
        RefreshToolStatus();
        ApplyQualityPreferences(AppPreferences.Load());
        Closed += async (_, _) => await _tools.DisposeAsync();
        Closed += (_, _) => App.Exporter.Dispose(); // sends what's still queued
        ViewModel.PropertyChanged += (_, e) =>
        {
            // The input box is disabled while a reply streams, which drops focus; hand it back.
            if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !ViewModel.IsBusy)
                DispatcherQueue.TryEnqueue(() => InputTextBox.Focus(FocusState.Programmatic));
        };

        // Finished replies and pending approvals notify the user when they're in another window.
        _attention = new AttentionNotifier(WindowHandle, App.IsPackaged, () => DispatcherQueue.TryEnqueue(BringToFront));
        Closed += (_, _) => _attention.Dispose();
        ViewModel.TurnFinished += (_, e) => _attention.Notify(
            e.Succeeded ? Strings.NotifyReplyDone : Strings.NotifyReplyFailed, PlainPreview(e.Text));
        ViewModel.ApprovalRequested += (_, tool) => _attention.Notify(Strings.NotifyApprovalTitle, Strings.NotifyApprovalBody(tool));

        ConfigureZoom();
        ConfigureShortcuts();
        ConfigureAttachments();
        ConfigureToolbar();
        ResizeWindow(960, 720);
        InitializeAsync();
    }

    private readonly AttentionNotifier _attention;

    private void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    /// <summary>A reply as one line of plain text for a notification (no Markdown symbols or artifact markup).</summary>
    private static string PlainPreview(string text)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(text, @"<artifact\b[\s\S]*?(</artifact>|$)", "");
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"[#*_`>|]+", "");
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length > 0 ? plain : Strings.NotifyReplyDone;
    }

    /// <summary>History is optional: if the database can't be opened (locked, corrupt), chat still works.</summary>
    private static ConversationStore? OpenHistory()
    {
        try
        {
            return new ConversationStore();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Conversation history unavailable: {ex}");
            return null;
        }
    }

    /// <summary>Bool-negation helper for x:Bind — WinUI/x:Bind has no built-in negation converter.</summary>
    public static bool IsNotBusy(bool isBusy) => !isBusy;

    public static Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private double Scale => GetDpiForWindow(WindowHandle) / 96.0;

    private void RefreshToolStatus() =>
        ViewModel.UpdateToolStatus(_tools.Mcp.Servers, _tools.BuiltInToolCount, _tools.SandboxRoot);

    private void ResizeWindow(int width, int height) =>
        AppWindow.Resize(new SizeInt32((int)(width * Scale), (int)(height * Scale)));

    private async void InitializeAsync()
    {
        await ChatWebView.EnsureCoreWebView2Async();
        ConfigureChatWebView();
        await LoadShellAsync();
        await ApplyZoomAsync();
        await ExecuteShellScriptAsync($"setDropHint({JsonSerializer.Serialize(Strings.DropHint)}); setCopyLabel({JsonSerializer.Serialize(Strings.CopyTooltip)});");
        await ViewModel.InitializeAsync();
        InputTextBox.Focus(FocusState.Programmatic);

        _ = StartMcpAsync();

        if (_needsProviderSetup)
            await PromptInitialSetupAsync();
    }

    /// <summary>Starts MCP servers in the background; chat works meanwhile, tools join from the next message.</summary>
    private async Task StartMcpAsync()
    {
        try
        {
            var servers = McpConfig.Load();
            await Task.Run(() => _tools.Mcp.StartAsync(servers));
        }
        catch (Exception ex)
        {
            await ViewModel.NotifyProviderErrorAsync(Strings.McpConfigReadFailed(McpConfig.DefaultPath, ex.Message));
        }
    }

    #region Dialogs

    private async void HistoryFlyout_Opening(object sender, object e)
    {
        HistorySearchBox.Text = string.Empty;
        await ReloadHistoryAsync();
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ConversationItem item)
            return;
        HistoryFlyout.Hide();
        await ViewModel.OpenConversationAsync(item.Id);
    }

    private async void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            await ViewModel.DeleteConversationAsync(id);
            UpdateHistoryEmptyText();
        }
    }

    private async void ToolsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDialogOpen)
            return;
        var dialog = new McpDialog(_tools) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// WinUI allows one ContentDialog at a time and throws (crashing the app) on a second, e.g. the
    /// settings or MCP button clicked while an MCP App approval is showing.
    /// </summary>
    private bool IsDialogOpen =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => p.Child is ContentDialog);

    /// <summary>
    /// Shown automatically after the shell loads when nothing is configured anywhere. Saving builds a
    /// ChatSession immediately; cancelling falls back to the "not connected" bubble.
    /// </summary>
    private async Task PromptInitialSetupAsync()
    {
        if (!await ShowSettingsAsync())
            await ViewModel.NotifyNotConfiguredAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDialogOpen)
            await ShowSettingsAsync();
    }

    #region Response quality

    private void ApplyQualityPreferences(AppPreferences preferences)
    {
        ViewModel.ReasoningEffort = preferences.ReasoningEffort;
        ViewModel.ImageQuality = _tools.ImageQuality = ImageGenerationTool.NormalizeQuality(preferences.ImageQuality);
        ViewModel.WebSearch = preferences.WebSearch;
        ToolTipService.SetToolTip(QualityButton, Strings.QualityTooltip(EffortLabel(preferences.ReasoningEffort), ImageLabel(ViewModel.ImageQuality)));
    }

    private void QualityFlyout_Opening(object sender, object e)
    {
        var effort = AppPreferences.ParseEffort(ViewModel.ReasoningEffort) is null ? "auto" : ViewModel.ReasoningEffort;
        foreach (var item in new[] { EffortAuto, EffortLow, EffortMedium, EffortHigh })
            item.IsChecked = (string)item.Tag == effort;
        foreach (var item in new[] { ImageLow, ImageMedium, ImageHigh })
            item.IsChecked = (string)item.Tag == ViewModel.ImageQuality;

        // The provider's own search: only on the Responses API (OpenAI, Azure OpenAI).
        WebSearchItem.IsEnabled = ViewModel.SupportsWebSearch;
        WebSearchItem.IsChecked = ViewModel.WebSearch && ViewModel.SupportsWebSearch;
        WebSearchItem.Text = ViewModel.SupportsWebSearch ? Strings.WebSearch : Strings.WebSearchUnsupported;
        ToolTipService.SetToolTip(WebSearchItem, Strings.WebSearchNote);
    }

    private void WebSearch_Click(object sender, RoutedEventArgs e) =>
        SaveQuality(p => p with { WebSearch = WebSearchItem.IsChecked });

    private void Effort_Click(object sender, RoutedEventArgs e) =>
        SaveQuality(p => p with { ReasoningEffort = (string)((FrameworkElement)sender).Tag });

    private void ImageQuality_Click(object sender, RoutedEventArgs e) =>
        SaveQuality(p => p with { ImageQuality = (string)((FrameworkElement)sender).Tag });

    private void SaveQuality(Func<AppPreferences, AppPreferences> change)
    {
        var preferences = change(AppPreferences.Load());
        try
        {
            preferences.Save();
        }
        catch (IOException)
        {
            // Still applies for this session.
        }

        ApplyQualityPreferences(preferences);
    }

    private static string EffortLabel(string effort) => effort switch
    {
        "low" => Strings.EffortLow,
        "medium" => Strings.EffortMedium,
        "high" => Strings.EffortHigh,
        _ => Strings.EffortAuto,
    };

    private static string ImageLabel(string quality) => quality switch
    {
        "medium" => Strings.ImageMedium,
        "high" => Strings.ImageHigh,
        _ => Strings.ImageLow,
    };

    #endregion

    /// <summary>
    /// Opens Settings pre-filled from whatever's currently active (never the API key itself) and applies
    /// what was saved. Returns whether the user saved.
    /// </summary>
    private async Task<bool> ShowSettingsAsync()
    {
        var saved = LocalSettingsStore.Load();
        var dialog = new SettingsDialog(
            saved?.Endpoint ?? _options.Provider.Endpoint,
            saved?.Model ?? _options.Provider.Model,
            saved?.Api ?? ProviderApiExtensions.Parse(_options.Provider.Api),
            saved is not null ? saved.ImageModel : _options.Provider.ImageModel,
            hasExistingApiKey: saved is not null,
            WindowHandle,
            _permissions)
        {
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Saved is not { } updated)
            return false;

        // Rebuilding the session drops the conversation, so only do it when the connection changed
        // (not for a language, sandbox or image-model change).
        var connectionChanged = !ViewModel.HasSession || saved is null ||
            (saved.Endpoint, saved.Model, saved.ApiKey, saved.Api) != (updated.Endpoint, updated.Model, updated.ApiKey, updated.Api);
        if (connectionChanged)
            await ApplySavedProviderAsync(updated);
        else
            _tools.SetImageGeneration(App.ImageSettingsFor(updated));

        if (dialog.NewSandboxFolder is { } sandbox)
            _tools.SetSandbox(sandbox);
        if (dialog.LanguageChanged)
            await OfferRestartAsync();
        return true;
    }

    /// <summary>UI strings are picked at startup, so a language change needs a restart.</summary>
    private async Task OfferRestartAsync()
    {
        // Shown in both languages: the current strings are still the old language.
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Restart Harness.WinUI? / 要重新啟動 Harness.WinUI 嗎？",
            Content = "Restart now to apply the new language?\n要現在重新啟動以套用新的語言嗎？",
            PrimaryButtonText = "Restart / 重新啟動",
            CloseButtonText = "Later / 稍後",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && Environment.ProcessPath is { } exe)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Close(); // Closed disposes the MCP servers; closing the last window ends this instance.
        }
    }

    /// <summary>Rebuilds the ChatSession from newly-saved settings and swaps it into the live ViewModel.</summary>
    private async Task ApplySavedProviderAsync(ResolvedProvider provider)
    {
        try
        {
            ViewModel.UpdateSession(new ChatSession(
                ChatClientFactory.Create(provider), _options, () => _tools.All, ChatClientFactory.SupportsReasoningSummaries(provider)));
            _tools.SetImageGeneration(App.ImageSettingsFor(provider));
        }
        catch (Exception ex)
        {
            await ViewModel.NotifyProviderErrorAsync(Strings.ProviderConnectFailed(provider.Model, provider.Endpoint, ex.Message));
        }
    }

    #endregion

    #region Chat WebView

    private void ConfigureChatWebView()
    {
        var core = ChatWebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;

        // wwwroot (highlight.js, mermaid, css) ships next to the app — see Harness.WinUI.csproj.
        core.SetVirtualHostNameToFolderMapping(
            ChatShell.VirtualHostName, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);

        Directory.CreateDirectory(ImageGenerationTool.OutputDirectory);
        core.SetVirtualHostNameToFolderMapping(
            ChatShell.ImagesHostName, ImageGenerationTool.OutputDirectory, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        ConfigureMcpApps(core);

        // The chat page must never be replaced: anything that tries to navigate goes to the user's
        // browser instead — and only when the user actually clicked something.
        core.NavigationStarting += (_, e) =>
        {
            if (!_shellLoaded)
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
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var message = doc.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "toolApproval":
                    ViewModel.ResolveApproval(
                        message.GetProperty("callId").GetString() ?? string.Empty,
                        message.GetProperty("decision").GetString() ?? "deny");
                    break;
                case "openLink":
                    ShellLauncher.OpenLink(message.GetProperty("href").GetString());
                    break;
                case "openArtifact":
                    _ = ArtifactPanel.ShowAsync(message.GetProperty("id").GetString() ?? string.Empty);
                    break;
                case "openTrace":
                    OpenObservability(message.GetProperty("trace").GetString());
                    break;
                case "zoom":
                    StepZoom(message.GetProperty("step").GetInt32());
                    break;
                case "retry":
                    _ = ViewModel.RetryAsync();
                    break;
                case "shortcut":
                    RunShortcut(message.GetProperty("name").GetString() ?? string.Empty);
                    break;
                case "copy":
                    if (message.GetProperty("text").GetString() is { Length: > 0 } text)
                        CopyToClipboard(text);
                    break;
                case "dropFile":
                    OnDroppedFile(message.GetProperty("name").GetString() ?? "file", message.GetProperty("data").GetString() ?? string.Empty);
                    break;
                case "dropTooLarge":
                    ShowHint(Strings.AttachmentTooLarge(message.GetProperty("name").GetString() ?? "file"), 3000);
                    break;
                case "mcpApp":
                    OnAppMessage(message.GetProperty("view").GetString() ?? string.Empty, message.GetProperty("message").Clone());
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Malformed message from the page — ignore.
        }
    }

    private async Task LoadShellAsync()
    {
        var loaded = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            ChatWebView.CoreWebView2.NavigationCompleted -= Handler;
            loaded.SetResult();
        }

        ChatWebView.CoreWebView2.NavigationCompleted += Handler;
        ChatWebView.NavigateToString(ChatShell.GetShellHtml());
        await loaded.Task;
        _shellLoaded = true;
    }

    #endregion

    #region Composer

    // PreviewKeyDown, not KeyDown: a TextBox with AcceptsReturn consumes Enter itself, so a
    // KeyDown handler never sees it. While an IME is composing (注音/倉頡 etc.), Enter confirms
    // the candidate and arrives as VirtualKey.ProcessKey; the composition flag is a second guard.
    private void InputTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleSkillSuggestionKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Enter || _isComposing)
            return;

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(CoreVirtualKeyStates.Down))
            return; // Shift+Enter falls through to the TextBox and inserts a newline.

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
            ViewModel.SendCommand.Execute(null);
    }

    private void InputTextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _isComposing = true;

    private void InputTextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _isComposing = false;

    #endregion

    #region Artifact panel layout

    private void ArtifactPanel_OpenRequested(object? sender, EventArgs e)
    {
        if (ArtifactPanel.Visibility == Visibility.Visible)
            return;

        ArtifactPanel.Visibility = Visibility.Visible;
        ArtifactSplitter.Visibility = Visibility.Visible;
        ChatColumn.Width = new GridLength(1, GridUnitType.Star);
        ArtifactColumn.Width = new GridLength(1.2, GridUnitType.Star);
        ArtifactColumn.MinWidth = 320;
        WidenWindowForArtifacts();
    }

    private void ArtifactPanel_CloseRequested(object? sender, EventArgs e) => CloseArtifactPanel();

    private void CloseArtifactPanel()
    {
        ArtifactPanel.Visibility = Visibility.Collapsed;
        ArtifactSplitter.Visibility = Visibility.Collapsed;
        ArtifactColumn.MinWidth = 0;
        ArtifactColumn.Width = new GridLength(0);
        ChatColumn.Width = new GridLength(1, GridUnitType.Star);
        ArtifactPanel.Unload();
    }

    /// <summary>At the default 960px width, chat + panel would both be cramped; grow the window if there's room.</summary>
    private void WidenWindowForArtifacts()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            return;

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var target = Math.Min((int)(1440 * Scale), workArea.Width);
        if (AppWindow.Size.Width >= target)
            return;

        var x = Math.Clamp(AppWindow.Position.X, workArea.X, workArea.X + workArea.Width - target);
        AppWindow.MoveAndResize(new RectInt32(x, AppWindow.Position.Y, target, AppWindow.Size.Height));
    }

    #endregion

    #region IChatMessageSink

    public Task AppendMessageAsync(string id, string role, string html) =>
        ExecuteShellScriptAsync($"appendMessage({ToJs(id)}, {ToJs(role)}, {ToJs(html)});");

    public Task UpdateMessageContentAsync(string id, string html, bool isFinal) =>
        ExecuteShellScriptAsync($"updateMessageContent({ToJs(id)}, {ToJs(html)}, {(isFinal ? "true" : "false")});");

    public Task SetMessageStatusAsync(string id, string? status, bool paused = false) =>
        ExecuteShellScriptAsync($"setMessageStatus({ToJs(id)}, {ToJs(status)}, {(paused ? "true" : "false")});");

    public Task UpsertMessageStepAsync(string id, string stepId, string html, StepState state) =>
        ExecuteShellScriptAsync(
            $"upsertMessageStep({ToJs(id)}, {ToJs(stepId)}, {ToJs(html)}, {ToJs(state.ToString().ToLowerInvariant())});");

    public Task SetMessageMetaAsync(string id, string text, string? tooltip, string? traceId = null) =>
        ExecuteShellScriptAsync($"setMessageMeta({ToJs(id)}, {ToJs(text)}, {ToJs(tooltip)}, {ToJs(traceId)});");

    public Task PresentArtifactAsync(ArtifactSegment artifact) =>
        RunOnUIThreadAsync(() => ArtifactPanel.PresentAsync(artifact));

    public Task RestoreArtifactAsync(ArtifactSegment artifact) =>
        RunOnUIThreadAsync(() =>
        {
            ArtifactPanel.Register(artifact);
            return Task.CompletedTask;
        });

    public Task ClearConversationAsync() =>
        RunOnUIThreadAsync(async () =>
        {
            CloseArtifactPanel();
            await TeardownAppViewsAsync();
            await ExecuteShellScriptAsync("clearMessages();");
            InputTextBox.Focus(FocusState.Programmatic);
        });

    private Task ExecuteShellScriptAsync(string script) =>
        RunOnUIThreadAsync(async () =>
        {
            if (_shellLoaded && ChatWebView.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync(script);
        });

    /// <summary>JSON-encodes a string into a JS string literal safe to splice into an ExecuteScriptAsync call.</summary>
    private static string ToJs(string? value) => JsonSerializer.Serialize(value ?? string.Empty);

    /// <summary>
    /// Marshals work onto this window's UI thread. The ViewModel's streaming loop and tool approvals run
    /// continuations off the UI thread (ChatSession uses ConfigureAwait(false)), so sink callers can't
    /// assume they're already on it.
    /// </summary>
    private Task RunOnUIThreadAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess)
            return action();

        var done = new TaskCompletionSource();
        var enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        if (!enqueued)
            done.SetException(new InvalidOperationException("Failed to enqueue work on the UI thread dispatcher."));

        return done.Task;
    }

    #endregion
}
