// Yoke — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using Yoke.Core.Artifacts;
using Yoke.MarkdownRendering;

namespace Yoke.App.Artifacts;

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

    public ArtifactPanel() => InitializeComponent();

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
        SetHeader(stored.Title, stored.Type, stored.Version);
        CodeBox.Text = stored.Content;

        await EnsureWebViewAsync();
        PreviewWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        PreviewWebView.CoreWebView2.Navigate(stored.PreviewUrl);
        SelectTab(code: false);
    }

    /// <summary>
    /// Artifact pages run untrusted scripts: own origin (yoke.artifacts), no web-message bridge or host
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
            _ => "HTML",
        };
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

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string SafeFileName(string title, string fallback)
    {
        var cleaned = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}
