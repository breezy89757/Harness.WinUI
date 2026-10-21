// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using Harness.Core.Artifacts;
using Harness.Core.Config;
using Harness.Core.Files;
using Harness.Core.Providers;
using Harness.MarkdownRendering;

namespace Harness.WinUI.Artifacts;

/// <summary>
/// Side panel showing one artifact: its code while it streams in, then a rendered preview. The host
/// (<see cref="MainWindow"/>) owns the layout and reacts to <see cref="OpenRequested"/> /
/// <see cref="CloseRequested"/>; this control owns the artifact store and its sandboxed WebView2.
/// </summary>
public sealed partial class ArtifactPanel : UserControl
{
    private readonly ArtifactStore _store = new();
    private StoredArtifact? _shown;
    private string? _streamingId;
    private string? _dismissedId;
    private bool _webViewReady;

    public ArtifactPanel()
    {
        InitializeComponent();
        _store.Theme = _store.Themes.Resolve(AppPreferences.Load().SlideTheme);
    }

    /// <summary>The panel wants to be visible (an artifact started streaming or was opened).</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? CloseRequested;

    /// <summary>Owner window handle, needed by the Save As picker.</summary>
    public nint WindowHandle { get; set; }

    /// <summary>Live content while streaming; once complete, saved and rendered.</summary>
    public async Task PresentAsync(ArtifactSegment artifact)
    {
        // If the user closed the panel while this artifact was streaming, keep it closed.
        var show = artifact.Id != _dismissedId;

        if (!artifact.IsComplete)
        {
            _streamingId = artifact.Id;
            if (!show)
                return;

            OpenRequested?.Invoke(this, EventArgs.Empty);
            SetHeader(artifact.Title, artifact.Type, version: null);
            CodeBox.Text = artifact.Content;
            if (artifact.Type == ArtifactType.Slides)
            {
                await ShowLiveSlidesAsync(artifact); // each slide appears as soon as it is written
                return;
            }

            SelectTab(code: true);
            ScrollToEnd(CodeBox);
            return;
        }

        _streamingId = null;
        var stored = _store.Save(artifact);
        if (show)
            await ShowAsync(stored);
        else
            _dismissedId = null;
    }

    private const string LiveSlidesFile = "_live-slides.html";
    private string? _liveSlidesId;
    private bool _liveSlidesReady;
    private DateTime _liveSlidesAt;

    /// <summary>Shows a deck that is still being written: the preview page is loaded once, then given the slides as they come.</summary>
    private async Task ShowLiveSlidesAsync(ArtifactSegment artifact)
    {
        SelectTab(code: false);
        await EnsureWebViewAsync();
        var core = PreviewWebView.CoreWebView2;
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        if (_liveSlidesId != artifact.Id)
        {
            _liveSlidesId = artifact.Id;
            _liveSlidesReady = false;
            core.Settings.IsWebMessageEnabled = false;
            Directory.CreateDirectory(ArtifactStore.Folder);
            _store.Themes.PublishAssets(_store.Theme, ArtifactStore.Folder);
            File.WriteAllText(Path.Combine(ArtifactStore.Folder, LiveSlidesFile), SlidesPreview.Page(artifact.Title, SlideDeck.Parse(artifact.Content, isComplete: false).ToJson(_store.Theme), follow: true));
            core.Navigate($"https://{ArtifactStore.HostName}/{LiveSlidesFile}?id={Uri.EscapeDataString(artifact.Id)}");
            return;
        }

        if (!_liveSlidesReady || (DateTime.UtcNow - _liveSlidesAt).TotalMilliseconds < 250)
            return;
        _liveSlidesAt = DateTime.UtcNow;
        await core.ExecuteScriptAsync($"window.setDeck({SlideDeck.Parse(artifact.Content, isComplete: false).ToJson(_store.Theme)})");
    }

    private readonly Dictionary<string, (string Title, string Content)> _editNotices = new(StringComparer.Ordinal);

    /// <summary>What the person changed in a deck since the last message, worded for the model (it would otherwise write its old version again); null when nothing changed.</summary>
    public string? TakeEditNotice()
    {
        if (_editNotices.Count == 0)
            return null;
        var notice = string.Join("\n\n", _editNotices.Select(e =>
            $"[The user edited the presentation \"{e.Value.Title}\" (artifact id {e.Key}) in the preview. This is its current content; when asked to change it, start from this version and output it in full with the same id:\n{e.Value.Content}]"));
        _editNotices.Clear();
        return notice;
    }

    /// <summary>The slides page says what the person edited (see <see cref="SlideEdit"/>); the deck is changed, saved, and for changes that need it the page is given the new deck.</summary>
    private async void OnSlidesMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_shown is not { Type: ArtifactType.Slides } shown ||
            !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            !source.Host.Equals(ArtifactStore.HostName, StringComparison.OrdinalIgnoreCase) ||
            Uri.UnescapeDataString(source.AbsolutePath).TrimStart('/') != shown.PreviewFile)
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var next = SlideEdit.Apply(SlideDeck.Parse(shown.Content, isComplete: true), message.RootElement, Strings.SlideNewTitle);
            var markdown = next.ToMarkdown();
            if (markdown != shown.Content)
            {
                var stored = _store.Save(new ArtifactSegment(shown.Id, ArtifactType.Slides, shown.Title, markdown, true), edited: true);
                _shown = stored;
                CodeBox.Text = markdown;
                _editNotices[stored.Id] = (stored.Title, markdown);
            }

            // A change of text is already on the page; a new line or a changed set of slides is drawn from the deck.
            if (message.RootElement.TryGetProperty("op", out var kind) && kind.GetString() != "text")
                await PreviewWebView.CoreWebView2.ExecuteScriptAsync($"window.setDeck({next.ToJson(_store.Theme)})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Log($"slide edit failed: {ex.Message}");
        }
    }

    /// <summary>Stores a completed artifact from a reopened conversation so its card can open it; doesn't show it.</summary>
    public void Register(ArtifactSegment artifact) => _store.Save(artifact);

    /// <summary>Opens a previously completed artifact (e.g. from its card in the chat).</summary>
    public async Task ShowAsync(string id)
    {
        _dismissedId = null;
        if (_store.Get(id) is { } stored)
            await ShowAsync(stored);
        else
            OpenRequested?.Invoke(this, EventArgs.Empty); // still being written
    }

    /// <summary>Called by the host after hiding the panel: stop the page's scripts and let WebView2 trim memory.</summary>
    public void Unload()
    {
        _dismissedId = _streamingId;
        _shown = null;
        _liveSlidesId = null;
        _liveSlidesReady = false;
        if (_webViewReady)
        {
            PreviewWebView.CoreWebView2.Navigate("about:blank");
            PreviewWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
    }

    private async Task ShowAsync(StoredArtifact stored)
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
        _shown = stored;
        _liveSlidesId = null;
        _liveSlidesReady = false;
        SetHeader(stored.Title, stored.Type, stored.Version);
        CodeBox.Text = stored.Content;

        await EnsureWebViewAsync();
        PreviewWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        // Only the slides page, which is ours, may talk to the app (it sends what the person edited); an HTML artifact never can.
        PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = stored.Type == ArtifactType.Slides;
        PreviewWebView.CoreWebView2.Navigate(stored.PreviewUrl);
        SelectTab(code: false);
    }

    /// <summary>
    /// Artifact pages run untrusted scripts: own origin (harness.artifacts), no web-message bridge or host
    /// objects, and leaving the artifact is only allowed for links the user actually clicked.
    /// </summary>
    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
            return;

        await PreviewWebView.EnsureCoreWebView2Async();
        var core = PreviewWebView.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        Directory.CreateDirectory(ArtifactStore.Folder);
        core.SetVirtualHostNameToFolderMapping(ArtifactStore.HostName, ArtifactStore.Folder, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            ChatShell.VirtualHostName, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnSlidesMessage;
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess && core.Source.Contains(LiveSlidesFile, StringComparison.Ordinal))
                _liveSlidesReady = true;
        };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank" ||
                (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Host.Equals(ArtifactStore.HostName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

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

        _webViewReady = true;
    }

    private void SetHeader(string title, ArtifactType type, int? version)
    {
        TitleText.Text = title;
        var typeLabel = type switch
        {
            ArtifactType.Svg => "SVG",
            ArtifactType.Mermaid => Strings.ArtifactDiagram,
            ArtifactType.Markdown => Strings.ArtifactDocument,
            ArtifactType.Slides => Strings.ArtifactSlides,
            _ => "HTML",
        };
        ExportPowerPointButton.Visibility = type == ArtifactType.Slides && version is not null ? Visibility.Visible : Visibility.Collapsed;
        ThemeButton.Visibility = ExportPowerPointButton.Visibility;
        SubtitleText.Text = $"{typeLabel} · {(version is { } v ? Strings.ArtifactVersion(v) : Strings.ArtifactWriting)}";
    }

    private void SelectTab(bool code) => Tabs.SelectedItem = code ? CodeTab : PreviewTab;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var code = sender.SelectedItem == CodeTab;
        CodeBox.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        PreviewWebView.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ScrollToEnd(TextBox textBox)
    {
        if (FindDescendant<ScrollViewer>(textBox) is { } scroller)
            scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is { } artifact && File.Exists(artifact.PreviewPath))
            ShellLauncher.OpenFile(artifact.PreviewPath);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } artifact)
            return;

        var extension = Path.GetExtension(artifact.SourceFile);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeFileName(artifact.Title, artifact.Id),
        };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        try
        {
            if (await picker.PickSaveFileAsync() is { } file)
                File.Copy(artifact.SourcePath, file.Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SubtitleText.Text = ex.Message;
        }
    }

    private async void ExportPowerPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { Type: ArtifactType.Slides } artifact)
            return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeFileName(artifact.Title, artifact.Id),
        };
        picker.FileTypeChoices.Add("PowerPoint", [".pptx"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        try
        {
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            var theme = _store.Theme;
            var template = _store.Themes.TemplatePathOf(theme);
            await Task.Run(() => PowerPointWriter.WritePresentation(file.Path, SlideDeck.Parse(artifact.Content, isComplete: true), artifact.Title, theme, template));
            SubtitleText.Text = $"{Strings.ExportedPowerPoint} · {file.Name}";
            ShellLauncher.OpenFile(file.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SubtitleText.Text = Strings.ExportPowerPointFailed(ex.Message);
        }
    }

    /// <summary>The menu of company themes: choose one (the deck is drawn and exported in it), import a template, remove a theme.</summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var themes = _store.Themes.List();
        foreach (var theme in themes)
        {
            var item = new ToggleMenuFlyoutItem { Text = theme.IsBuiltIn ? Strings.ThemeBuiltIn : theme.Name, IsChecked = theme.Id == _store.Theme.Id };
            item.Click += (_, _) => UiGuard.Run(() => ChooseThemeAsync(theme), "choose theme");
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var import = new MenuFlyoutItem { Text = Strings.ThemeImport, Icon = new FontIcon { Glyph = "\uE8E5" } };
        import.Click += (_, _) => UiGuard.Run(ImportThemeAsync, "import theme");
        flyout.Items.Add(import);
        if (!_store.Theme.IsBuiltIn)
        {
            var analyze = new MenuFlyoutItem { Text = Strings.ThemeAnalyze, Icon = new FontIcon { Glyph = "\uE9D9" } };
            analyze.Click += (_, _) => UiGuard.Run(() => AnalyzeThemeAsync(_store.Theme), "analyze theme");
            flyout.Items.Add(analyze);
            if (File.Exists(_store.Themes.RulesPathOf(_store.Theme.Id)))
            {
                var edit = new MenuFlyoutItem { Text = Strings.ThemeEditRules, Icon = new FontIcon { Glyph = "\uE70F" } };
                edit.Click += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{_store.Themes.RulesPathOf(_store.Theme.Id)}\"") { UseShellExecute = false });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        SubtitleText.Text = ex.Message;
                    }
                };
                flyout.Items.Add(edit);
            }
        }

        if (themes.Count > 1)
        {
            var remove = new MenuFlyoutSubItem { Text = Strings.ThemeDelete };
            foreach (var theme in themes.Where(t => !t.IsBuiltIn))
            {
                var item = new MenuFlyoutItem { Text = theme.Name };
                item.Click += (_, _) => UiGuard.Run(async () =>
                {
                    _store.Themes.Delete(theme.Id);
                    if (theme.Id == _store.Theme.Id)
                        await ChooseThemeAsync(BrandTheme.BuiltIn);
                }, "delete theme");
                remove.Items.Add(item);
            }
            flyout.Items.Add(remove);
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    private async Task ChooseThemeAsync(BrandTheme theme)
    {
        _store.Theme = theme;
        (AppPreferences.Load() with { SlideTheme = theme.IsBuiltIn ? null : theme.Id }).Save();
        if (_shown is { Type: ArtifactType.Slides } shown)
            await ShowAsync(_store.Save(new ArtifactSegment(shown.Id, ArtifactType.Slides, shown.Title, shown.Content, true), edited: true));
    }

    /// <summary>Has the model read the template and write the theme's design guide (design.md); the guide is part of the instructions while the theme is in use.</summary>
    private async Task AnalyzeThemeAsync(BrandTheme theme)
    {
        if (theme.IsBuiltIn || _store.Themes.TemplatePathOf(theme) is not { } template)
            return;

        SubtitleText.Text = Strings.ThemeAnalyzing(theme.Name);
        try
        {
            if (ProviderResolver.TryResolve(null) is not { } provider)
            {
                SubtitleText.Text = Strings.ThemeAnalysisNoModel;
                return;
            }

            using var client = ChatClientFactory.Create(provider, instrument: false);
            var guide = await BrandThemeAnalyzer.AnalyzeAsync(client, theme, template);
            if (guide is null)
            {
                SubtitleText.Text = Strings.ThemeAnalysisEmpty;
                return;
            }

            _store.Themes.SaveRules(theme.Id, guide);
            SubtitleText.Text = Strings.ThemeAnalyzed(theme.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SubtitleText.Text = Strings.ThemeAnalysisFailed(ex.Message);
        }
    }

    private async Task ImportThemeAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".pptx");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        try
        {
            if (await picker.PickSingleFileAsync() is not { } file)
                return;
            // Downloaded templates often carry a date after the name (name.pptx_2022-08-31-05-16-46): the name is what comes before.
            var name = System.Text.RegularExpressions.Regex.Replace(file.Name, @"\.pptx(_.*)?$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var theme = await Task.Run(() => _store.Themes.Import(file.Path, name));
            await ChooseThemeAsync(theme);
            SubtitleText.Text = Strings.ThemeImported(theme.Name);
            // The design rules, in the background (the theme works without them); rules kept from an earlier theme of this name are used as they are.
            if (_store.Themes.ReadRules(theme.Id) is null)
                _ = AnalyzeThemeAsync(theme);
            else
                SubtitleText.Text = Strings.ThemeRulesKept(theme.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SubtitleText.Text = Strings.ThemeImportFailed(ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string SafeFileName(string title, string fallback)
    {
        var cleaned = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}
