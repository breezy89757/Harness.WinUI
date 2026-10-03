// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>The OTLP export settings: endpoint, protocol, headers (kept encrypted) and whether to send metrics.</summary>
public sealed partial class ObservabilityWindow
{
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Hide();
        var preferences = AppPreferences.Load();
        var hasHeaders = HeadersSaved();

        var enabled = new ToggleSwitch { Header = Strings.ExportEnabled, IsOn = preferences.OtlpEnabled };
        var endpoint = new TextBox
        {
            Header = Strings.ExportEndpoint,
            Text = preferences.OtlpEndpoint ?? string.Empty,
            PlaceholderText = "http://localhost:4318",
        };
        var protocol = new ComboBox { Header = Strings.ExportProtocol, MinWidth = 200 };
        protocol.Items.Add(new ComboBoxItem { Content = "HTTP/protobuf", Tag = "http" });
        protocol.Items.Add(new ComboBoxItem { Content = "gRPC", Tag = "grpc" });
        protocol.SelectedIndex = preferences.OtlpProtocol == "grpc" ? 1 : 0;
        var headers = new PasswordBox
        {
            Header = Strings.ExportHeaders,
            PlaceholderText = hasHeaders ? Strings.ExportHeadersSaved : "Authorization=Basic …",
        };
        var removeHeaders = false;
        var remove = new HyperlinkButton { Content = Strings.ExportHeadersRemove, Visibility = hasHeaders ? Visibility.Visible : Visibility.Collapsed, Padding = new Thickness(0) };
        remove.Click += (_, _) =>
        {
            removeHeaders = true;
            remove.Visibility = Visibility.Collapsed;
            headers.PlaceholderText = "Authorization=Basic …";
        };
        var metrics = new CheckBox { Content = new TextBlock { Text = Strings.ExportMetrics, TextWrapping = TextWrapping.Wrap }, IsChecked = preferences.OtlpMetrics };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(Caption(Strings.ExportIntro));
        panel.Children.Add(enabled);
        panel.Children.Add(endpoint);
        panel.Children.Add(Caption(Strings.ExportEndpointHelp));
        panel.Children.Add(protocol);
        panel.Children.Add(headers);
        panel.Children.Add(Caption(Strings.ExportHeadersHelp));
        panel.Children.Add(remove);
        panel.Children.Add(metrics);
        panel.Children.Add(Caption(Strings.ExportContentNote));
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.ExportTitle,
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = Strings.Save,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var url = endpoint.Text.Trim();
            if (enabled.IsOn && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            {
                args.Cancel = true;
                error.Text = Strings.ExportEndpointInvalid;
                error.Visibility = Visibility.Visible;
                return;
            }

            if (!string.IsNullOrEmpty(headers.Password))
                SecretStore.Set(AppPreferences.OtlpHeadersSecret, headers.Password.Trim());
            else if (removeHeaders)
                SecretStore.RemoveByPrefix(AppPreferences.OtlpHeadersSecret);

            var saved = AppPreferences.Load() with
            {
                OtlpEnabled = enabled.IsOn,
                OtlpEndpoint = string.IsNullOrEmpty(url) ? null : url,
                OtlpProtocol = (string)((ComboBoxItem)protocol.SelectedItem).Tag,
                OtlpMetrics = metrics.IsChecked == true,
            };
            saved.Save();
            if (App.ApplyExport(saved) is { } failure)
            {
                args.Cancel = true;
                error.Text = Strings.ExportFailed(failure);
                error.Visibility = Visibility.Visible;
            }
        };
        await dialog.ShowAsync();
    }

    private static bool HeadersSaved()
    {
        try
        {
            SecretStore.Resolve(SecretStore.Reference(AppPreferences.OtlpHeadersSecret));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
