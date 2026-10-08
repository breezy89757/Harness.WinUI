// Harness.WinUI — Licensed under the MIT License.

using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Harness.WinUI;

/// <summary>The About box (More menu): the version, that the app is open source, and where to find the code, report a problem and read the policies.</summary>
public sealed partial class MainWindow
{
    private const string RepositoryUrl = "https://github.com/breezy89757/Harness.WinUI";

    /// <summary>The app's version without the build's commit suffix ("1.0.9+abc…" becomes "1.0.9").</summary>
    private static string AppVersion()
    {
        var informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? "?" : version;
    }

    private async void ShowAbout()
    {
        if (IsDialogOpen)
            return;

        var panel = new StackPanel { Spacing = 4, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.AboutVersion(AppVersion()),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(new TextBlock { Text = Strings.AboutBody, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        foreach (var (label, url) in new[]
        {
            (Strings.AboutSourceCode, RepositoryUrl),
            (Strings.AboutReportIssue, RepositoryUrl + "/issues"),
            (Strings.AboutPrivacy, RepositoryUrl + "/blob/main/PRIVACY.md"),
            (Strings.AboutThirdParty, RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md"),
        })
        {
            var link = new HyperlinkButton { Content = label, Padding = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(link, label);
            link.Click += (_, _) => ShellLauncher.OpenLink(url);
            panel.Children.Add(link);
        }

        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.AboutTitle,
            Content = panel,
            CloseButtonText = Strings.Close,
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
        InputTextBox.Focus(FocusState.Programmatic);
    }
}
