// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Harness.Core.Config;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>
/// App settings: UI language, model provider (endpoint, model, API key, API type, image model), the
/// sandbox folder the built-in file tools may access, and what's always allowed to run without asking. The API key is DPAPI-encrypted via
/// <see cref="LocalSettingsStore.Save"/>; this dialog never writes it in plaintext, logs it, or echoes
/// it back once saved.
///
/// The dialog persists settings but doesn't own the chat session: the caller reads <see cref="Saved"/>
/// and <see cref="LanguageChanged"/> after a Primary result.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    /// <summary>
    /// True when a key is already saved in <see cref="LocalSettingsStore"/> — lets the user leave the
    /// API Key field blank to keep the existing key instead of retyping it.
    /// </summary>
    private readonly bool _hasExistingApiKey;
    private readonly nint _windowHandle;
    private readonly string _initialLanguage;
    private readonly string _initialSandbox;
    private readonly ToolPermissionStore _permissions;
    private readonly List<(string ServerName, string ToolName)> _removedPermissions = [];

    public ResolvedProvider? Saved { get; private set; }

    public bool LanguageChanged { get; private set; }

    /// <summary>Set when the sandbox folder changed: the new folder, or empty when file tools were turned off.</summary>
    public string? NewSandboxFolder { get; private set; }

    public SettingsDialog(
        string? initialEndpoint, string? initialModel, ProviderApi initialApi, string? initialImageModel,
        bool hasExistingApiKey, nint windowHandle, ToolPermissionStore permissions)
    {
        InitializeComponent();

        _permissions = permissions;
        ShowAlwaysAllowed();

        _hasExistingApiKey = hasExistingApiKey;
        _windowHandle = windowHandle;

        EndpointTextBox.Text = initialEndpoint ?? string.Empty;
        ModelTextBox.Text = initialModel ?? string.Empty;
        ImageModelTextBox.Text = initialImageModel ?? string.Empty;
        ApiKeyPasswordBox.PlaceholderText = hasExistingApiKey ? Strings.ApiKeyKeep : Strings.ApiKeyEnter;
        SelectByTag(ApiComboBox, initialApi.ToString());

        var preferences = AppPreferences.Load();
        _initialLanguage = preferences.Language;
        SelectByTag(LanguageComboBox, _initialLanguage);

        _initialSandbox = preferences.SandboxFolder ?? string.Empty;
        SandboxTextBox.Text = _initialSandbox;

        CurrencyTextBox.Text = preferences.Currency;
        var price = preferences.PriceFor(initialModel);
        PriceInputBox.Value = price is null ? double.NaN : (double)price.Input;
        PriceCachedBox.Value = price is null ? double.NaN : (double)price.CachedInput;
        PriceOutputBox.Value = price is null ? double.NaN : (double)price.Output;

        UpdateSaveButtonState();
    }

    /// <summary>One row per always-allowed action, each with a Remove button (removals apply on Save).</summary>
    private void ShowAlwaysAllowed()
    {
        AlwaysAllowedPanel.Children.Clear();
        var entries = _permissions.List().Except(_removedPermissions).ToList();
        if (entries.Count == 0)
        {
            AlwaysAllowedPanel.Children.Add(new TextBlock { Text = Strings.AlwaysAllowedNone, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            return;
        }

        foreach (var entry in entries)
        {
            var label = Describe(entry.ServerName, entry.ToolName);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = Strings.Remove };
            Grid.SetColumn(remove, 1);
            AutomationProperties.SetName(remove, $"{Strings.Remove}: {label}");
            AutomationProperties.SetAutomationId(remove, $"SettingsAlwaysAllowedRemove_{entry.ServerName}/{entry.ToolName}");
            remove.Click += (_, _) =>
            {
                _removedPermissions.Add(entry);
                ShowAlwaysAllowed();
            };
            row.Children.Add(remove);
            AlwaysAllowedPanel.Children.Add(row);
        }
    }

    /// <summary>"Commands starting with “uv run”", or "write_file (files)".</summary>
    private static string Describe(string serverName, string toolName) =>
        serverName == CommandTool.ServerName && toolName.StartsWith(CommandTool.Name + " ", StringComparison.Ordinal)
            ? Strings.CommandsStartingWith(toolName[(CommandTool.Name.Length + 1)..])
            : $"{toolName} ({serverName})";

    private static void SelectByTag(ComboBox comboBox, string tag) =>
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase)) ?? comboBox.Items[0];

    private static string SelectedTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void Field_Changed(object sender, RoutedEventArgs e) => UpdateSaveButtonState();

    private void UpdateSaveButtonState()
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(EndpointTextBox.Text);
        var hasModel = !string.IsNullOrWhiteSpace(ModelTextBox.Text);
        var hasApiKey = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password) || _hasExistingApiKey;

        IsPrimaryButtonEnabled = hasEndpoint && hasModel && hasApiKey;
    }

    private async void BrowseSandbox_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

        if (await picker.PickSingleFolderAsync() is { } folder)
            SandboxTextBox.Text = folder.Path;
    }

    private void SettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var endpoint = EndpointTextBox.Text?.Trim() ?? string.Empty;
        var model = ModelTextBox.Text?.Trim() ?? string.Empty;
        var typedApiKey = ApiKeyPasswordBox.Password ?? string.Empty;
        var api = ProviderApiExtensions.Parse(SelectedTag(ApiComboBox));
        var imageModel = string.IsNullOrWhiteSpace(ImageModelTextBox.Text) ? null : ImageModelTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            args.Cancel = true;
            ShowError(Strings.EndpointModelRequired);
            return;
        }

        string apiKeyToSave;
        if (!string.IsNullOrWhiteSpace(typedApiKey))
        {
            apiKeyToSave = typedApiKey;
        }
        else if (_hasExistingApiKey && LocalSettingsStore.Load() is { } existing)
        {
            apiKeyToSave = existing.ApiKey;
        }
        else
        {
            args.Cancel = true;
            ShowError(Strings.ApiKeyRequired);
            return;
        }

        // Validate the sandbox folder before saving anything, so a bad path doesn't leave a half-saved state.
        var sandbox = SandboxTextBox.Text?.Trim() ?? string.Empty;
        var sandboxChanged = !string.Equals(sandbox, _initialSandbox, StringComparison.OrdinalIgnoreCase);
        if (sandboxChanged && sandbox.Length > 0)
        {
            try
            {
                sandbox = Path.GetFullPath(sandbox);
                Directory.CreateDirectory(sandbox);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                ShowError(Strings.SandboxFailed(ex.Message));
                return;
            }
        }

        var currency = (CurrencyTextBox.Text ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$"))
        {
            args.Cancel = true;
            ShowError(Strings.CurrencyInvalid);
            return;
        }

        try
        {
            LocalSettingsStore.Save(endpoint, model, apiKeyToSave, api, imageModel);

            var language = SelectedTag(LanguageComboBox);
            LanguageChanged = !string.Equals(language, _initialLanguage, StringComparison.OrdinalIgnoreCase);
            var current = AppPreferences.Load();
            (current with
            {
                Language = language,
                SandboxFolder = sandboxChanged ? sandbox : current.SandboxFolder,
                Currency = currency,
                ModelPrices = PricesWith(current.ModelPrices, model),
            }).Save();
            if (sandboxChanged)
                NewSandboxFolder = sandbox;
            foreach (var (serverName, toolName) in _removedPermissions)
                _permissions.Remove(serverName, toolName);
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowError(Strings.SaveFailed(ex.Message));
            return;
        }

        Saved = new ResolvedProvider(endpoint, model, apiKeyToSave, api, ProviderSource.LocalEncryptedSettings, imageModel);
    }

    /// <summary>
    /// The saved prices with this model's entry set from the three boxes (removed when all are empty).
    /// An empty cached price means no cache discount: cached tokens cost the same as input.
    /// </summary>
    private Dictionary<string, Harness.Core.Usage.ModelPrice>? PricesWith(Dictionary<string, Harness.Core.Usage.ModelPrice>? saved, string model)
    {
        var prices = new Dictionary<string, Harness.Core.Usage.ModelPrice>(saved ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var key in prices.Keys.Where(k => string.Equals(k, model, StringComparison.OrdinalIgnoreCase)).ToList())
            prices.Remove(key);

        static decimal? Read(NumberBox box) => double.IsNaN(box.Value) ? null : (decimal)Math.Max(0, box.Value);
        var input = Read(PriceInputBox);
        var cached = Read(PriceCachedBox);
        var output = Read(PriceOutputBox);
        if (input is not null || cached is not null || output is not null)
            prices[model] = new Harness.Core.Usage.ModelPrice(input ?? 0, cached ?? input ?? 0, output ?? 0);

        return prices.Count == 0 ? null : prices;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
