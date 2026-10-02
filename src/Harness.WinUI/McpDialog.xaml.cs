// Harness.WinUI — Licensed under the MIT License.

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>One server as shown in <see cref="McpDialog"/>.</summary>
public sealed class McpServerRow(McpServerStatus status)
{
    public string Name { get; } = status.Name;
    public string Summary { get; } = status.Summary;
    public bool Enabled { get; set; } = status.State != McpServerState.Disabled;

    public string StatusText { get; } = status.State switch
    {
        McpServerState.Connected => Strings.StatusConnected(status.ToolCount),
        McpServerState.Starting => Strings.StatusConnecting,
        McpServerState.Disabled => Strings.StatusDisabled,
        _ => Strings.StatusFailed(status.Error),
    };

    public Brush StateBrush { get; } = (Brush)Application.Current.Resources[status.State switch
    {
        McpServerState.Connected => "SystemFillColorSuccessBrush",
        McpServerState.Starting => "SystemFillColorCautionBrush",
        McpServerState.Failed => "SystemFillColorCriticalBrush",
        _ => "TextFillColorDisabledBrush",
    }];
}

/// <summary>
/// Lists MCP servers with live status, lets the user enable/disable/remove them and add a remote
/// (Streamable HTTP) server. Every change goes through <see cref="McpServerManager"/>, the same code
/// path the agent's own add/remove tools use, and applies to the running host immediately.
/// </summary>
public sealed partial class McpDialog : ContentDialog
{
    private readonly McpHost _host;
    private readonly ObservableCollection<McpServerRow> _rows = [];

    public McpDialog(McpHost host)
    {
        InitializeComponent();
        _host = host;
        ServerList.ItemsSource = _rows;
        Refresh();

        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
    }

    private void OnHostChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        _rows.Clear();
        foreach (var server in _host.Servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new McpServerRow(server));
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Enabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string name } toggle ||
            _rows.FirstOrDefault(r => r.Name == name) is not { } row ||
            row.Enabled == toggle.IsOn)
        {
            return; // initial binding, not a user change
        }

        // Read the control here: RunAsync runs the operation on a worker thread, where touching
        // the ToggleSwitch throws (wrong thread) and the change was silently never applied.
        var enable = toggle.IsOn;
        row.Enabled = enable;
        if (!await RunAsync(() => McpServerManager.SetEnabledAsync(_host, name, enable)))
        {
            row.Enabled = !enable;
            toggle.IsOn = !enable;
        }
    }

    /// <summary>Asks for confirmation in a small flyout (built in code: x:Bind inside a templated flyout crashes the XAML compiler).</summary>
    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } anchor)
            return;

        var flyout = new Flyout();
        var confirm = new Button { Content = Strings.Remove, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        confirm.Click += async (_, _) =>
        {
            flyout.Hide();
            if (await RunAsync(() => McpServerManager.RemoveAsync(_host, name)))
                ShowMessage(Strings.Removed(name), isError: false);
        };
        flyout.Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = Strings.RemoveConfirm(name) }, confirm } };
        flyout.ShowAt(anchor);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var url = UrlTextBox.Text.Trim();
        if (name.Length == 0 || url.Length == 0)
        {
            ShowMessage(Strings.NameUrlRequired, isError: true);
            return;
        }

        if (_host.Servers.Any(s => s.Name.Equals(name, StringComparison.Ordinal)))
        {
            ShowMessage(Strings.ServerExists(name), isError: true);
            return;
        }

        AddButton.IsEnabled = false;
        AddProgress.IsActive = true;
        ShowMessage(Strings.ConnectingTo(name), isError: false);
        try
        {
            var headerName = HeaderNameTextBox.Text;
            var headerValue = HeaderValueBox.Password;
            var status = await Task.Run(() => McpServerManager.AddHttpServerAsync(_host, name, url, headerName, headerValue));
            if (status.State == McpServerState.Connected)
            {
                ShowMessage(Strings.ConnectedWith(name, status.ToolCount), isError: false);
                NameTextBox.Text = UrlTextBox.Text = HeaderNameTextBox.Text = string.Empty;
                HeaderValueBox.Password = string.Empty;
            }
            else
            {
                ShowMessage(Strings.SavedButFailed(name, status.Error), isError: true);
            }
        }
        catch (ArgumentException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            ShowMessage(Strings.AddFailed(ex.Message), isError: true);
        }
        finally
        {
            AddButton.IsEnabled = true;
            AddProgress.IsActive = false;
        }
    }

    private void EditJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellLauncher.OpenFile(McpConfig.EnsureExists());

            ShowMessage(Strings.AfterEditingReload, isError: false);
        }
        catch (Exception ex)
        {
            ShowMessage(Strings.OpenMcpJsonFailed(ex.Message), isError: true);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        ShowMessage(Strings.Reloading, isError: false);
        if (await RunAsync(() => McpServerManager.ReloadAsync(_host)))
            ShowMessage(Strings.Reloaded, isError: false);
    }

    /// <summary>Runs a manager operation off the UI thread; reports failures in the dialog.</summary>
    private async Task<bool> RunAsync(Func<Task> operation)
    {
        try
        {
            await Task.Run(operation);
            return true;
        }
        catch (Exception ex)
        {
            // Some exceptions (e.g. COMException) come with an empty message; never show a blank error.
            ShowMessage(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message, isError: true);
            return false;
        }
    }

    private void ShowMessage(string message, bool isError)
    {
        MessageText.Text = message;
        MessageText.Foreground = (Brush)Application.Current.Resources[isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        MessageText.Visibility = Visibility.Visible;
    }
}
