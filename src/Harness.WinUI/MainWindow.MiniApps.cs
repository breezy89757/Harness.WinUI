// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Harness.Core.Config;
using Harness.Core.MiniApps;
using Harness.Core.Usage;
using Harness.WinUI.Artifacts;
using Harness.WinUI.MiniApps;

namespace Harness.WinUI;

/// <summary>
/// Pinned apps: an HTML artifact pinned as an app that opens in a window of its own (see <see cref="MiniAppStore"/>), the list to
/// open or unpin them, and what they may ask of Harness (<see cref="MiniAppBridge"/>).
/// </summary>
public sealed partial class MainWindow
{
    private readonly MiniAppStore _miniApps = new();
    private readonly MiniAppAudit _miniAudit = new();
    private readonly Dictionary<string, MiniAppWindow> _miniAppWindows = new(StringComparer.Ordinal);
    private UsageLedger? _miniLedger;

    /// <summary>Pins the shown artifact (again, as a new version when it was pinned before) and opens it.</summary>
    private async void ArtifactPanel_InstallAppRequested(object? sender, StoredArtifact artifact)
    {
        try
        {
            // The same artifact pinned before is updated rather than pinned twice.
            var existing = _miniApps.List().FirstOrDefault(a => a.SourceArtifactId == artifact.Id);
            var app = existing is null
                ? _miniApps.Install(artifact.Content, artifact.Id, artifact.Title)
                : _miniApps.Update(existing.Id, artifact.Content) ?? existing;
            ApplyToolbar(HiddenToolbarButtons()); // the apps button shows once there is one
            ShowHint(Strings.AppPinned(app.Name), 1800);
            await OpenMiniAppAsync(app);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log(Strings.AppInstallFailed(ex.Message));
            ShowHint(Strings.AppInstallFailed(ex.Message), 3500);
        }
    }

    /// <summary>
    /// Opens the app's window, or brings it forward (showing the newest version) when it is open. An app that asks for something
    /// (a model, say) first asks the person, once for each version.
    /// </summary>
    private async Task OpenMiniAppAsync(MiniAppInfo app)
    {
        if (!app.IsApproved)
            app = await AskToAllowAsync(app) ?? app;

        if (_miniAppWindows.TryGetValue(app.Id, out var open))
        {
            open.Reload(app);
            open.Activate();
            return;
        }

        var bridge = new MiniAppBridge(
            app.Id, _miniApps, MiniAppModel, (model, input, output) => RecordMiniAppUsage(app.Id, model, input, output),
            ShellLauncher.OpenLink, _miniAudit);
        var window = new MiniAppWindow(_miniApps, app, bridge);
        _miniAppWindows[app.Id] = window;
        window.Closed += (_, _) => _miniAppWindows.Remove(app.Id);
        window.Activate();
    }

    /// <summary>What the app asks for, put to the person. Allowing is for this version only; "not now" opens the app without it.</summary>
    private async Task<MiniAppInfo?> AskToAllowAsync(MiniAppInfo app)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = Strings.AppWantsTo(app.Name), TextWrapping = TextWrapping.Wrap });
        foreach (var permission in app.Permissions)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "•  " + Strings.AppPermission(permission),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
            });
        }

        panel.Children.Add(new TextBlock { Text = Strings.AppAllowNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.AppAllowTitle,
            Content = panel,
            PrimaryButtonText = Strings.Allow,
            CloseButtonText = Strings.AppNotNow,
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? _miniApps.Approve(app.Id) : null;
    }

    /// <summary>The model an app's calls go to: the one set up for the chat (recorded like any other call when recording is on).</summary>
    private (Microsoft.Extensions.AI.IChatClient? Client, string? Model) MiniAppModel() =>
        ProviderResolver.TryResolve(_options.Provider) is { } provider
            ? (Harness.Core.Providers.ChatClientFactory.Create(provider), provider.Model)
            : (null, null);

    /// <summary>An app's model calls cost like any other: they go in the usage totals, under the app's own name.</summary>
    private void RecordMiniAppUsage(string appId, string? model, long input, long output)
    {
        try
        {
            _miniLedger ??= new UsageLedger();
            var preferences = AppPreferences.Load();
            var cost = preferences.PriceFor(model)?.Cost(input, 0, output);
            _miniLedger.Record(new UsageEntry(DateTimeOffset.Now, "miniapp:" + appId, model, input, 0, output, cost, preferences.Currency));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("Usage of a pinned app could not be recorded: " + ex.Message);
        }
    }

    private void AppsButton_Click(object sender, RoutedEventArgs e) => _ = ShowMiniAppListAsync();

    private ContentDialog? _appsDialog;

    /// <summary>The pinned apps: open one, or unpin it (asked once more, since its data goes with it).</summary>
    private async Task ShowMiniAppListAsync()
    {
        if (_appsDialog is not null)
            return;

        // Opening an app may ask something of its own, and only one dialog can be up: it runs after this one has closed.
        MiniAppInfo? toOpen = null;
        var list = new StackPanel { Spacing = 8, MinWidth = 420 };
        AutomationProperties.SetAutomationId(list, "MiniAppList");
        var dialog = _appsDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.MyApps,
            Content = new ScrollViewer { Content = list, MaxHeight = 460 },
            CloseButtonText = Strings.Close,
            DefaultButton = ContentDialogButton.Close,
        };

        void Fill()
        {
            list.Children.Clear();
            var apps = _miniApps.List();
            if (apps.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = Strings.NoAppsYet, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
                return;
            }

            foreach (var app in apps)
                list.Children.Add(Row(app));
        }

        UIElement Row(MiniAppInfo app)
        {
            var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock
            {
                Text = Strings.AppPinnedOn(app.InstalledAt.LocalDateTime, app.Permissions, app.IsApproved),
                FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var open = new Button { Content = Strings.Open, MinWidth = 72 };
            AutomationProperties.SetAutomationId(open, "MiniAppOpen");
            open.Click += (_, _) =>
            {
                toOpen = _miniApps.Get(app.Id) ?? app;
                dialog.Hide();
            };

            var unpin = new Button { Content = Strings.Unpin };
            AutomationProperties.SetAutomationId(unpin, "MiniAppUnpin");
            unpin.Click += async (_, _) =>
            {
                if (unpin.Tag is null)
                {
                    // Once more: its saved data and versions go with it.
                    unpin.Tag = true;
                    unpin.Content = Strings.AppUnpinConfirm;
                    return;
                }

                unpin.IsEnabled = false;
                if (_miniAppWindows.TryGetValue(app.Id, out var window))
                    window.Close();
                await _miniApps.RemoveAsync(app.Id);
                ApplyToolbar(HiddenToolbarButtons());
                Fill();
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(details);
            Grid.SetColumn(open, 1);
            Grid.SetColumn(unpin, 2);
            row.Children.Add(open);
            row.Children.Add(unpin);
            return row;
        }

        Fill();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _appsDialog = null;
        }

        if (toOpen is not null)
            await OpenMiniAppAsync(toOpen);
    }
}
