// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
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
    /// <summary>A connection as it's being edited: the dialog keeps all of them in memory and saves them together.</summary>
    private sealed class Draft
    {
        public string Name = string.Empty;
        public string Endpoint = string.Empty;
        public string Model = string.Empty;
        public string ApiKey = string.Empty;
        public string ImageModel = string.Empty;
        public ProviderApi Api = ProviderApi.Auto;
    }

    private readonly List<Draft> _drafts;
    private int _current = -1;

    /// <summary>True while the boxes are being filled from a connection, so that isn't taken as the user editing.</summary>
    private bool _loading;
    private readonly Dictionary<string, Harness.Core.Usage.ModelPrice> _prices;
    private readonly nint _windowHandle;
    private readonly string _initialLanguage;
    private readonly string _initialSandbox;
    private readonly ToolPermissionStore _permissions;
    private readonly List<(string ServerName, string ToolName)> _removedPermissions = [];

    /// <summary>The connection that was showing when the user saved: the one to use now.</summary>
    public ResolvedProvider? Saved { get; private set; }

    public bool LanguageChanged { get; private set; }

    /// <summary>Set when the sandbox folder changed: the new folder, or empty when file tools were turned off.</summary>
    public string? NewSandboxFolder { get; private set; }

    /// <param name="profiles">The saved connections (keys included, never shown); at least one, possibly half-empty.</param>
    /// <param name="active">Index of the connection to show first.</param>
    public SettingsDialog(IReadOnlyList<ProviderProfile> profiles, int active, nint windowHandle, ToolPermissionStore permissions)
    {
        InitializeComponent();

        _permissions = permissions;
        ShowAlwaysAllowed();

        _windowHandle = windowHandle;

        var preferences = AppPreferences.Load();
        _prices = new Dictionary<string, Harness.Core.Usage.ModelPrice>(preferences.ModelPrices ?? [], StringComparer.OrdinalIgnoreCase);

        _drafts = profiles.Select(p => new Draft
        {
            Name = p.Name, Endpoint = p.Endpoint, Model = p.Model, ApiKey = p.ApiKey, ImageModel = p.ImageModel ?? string.Empty, Api = p.Api,
        }).ToList();
        _loading = true;
        foreach (var draft in _drafts)
            ProfileComboBox.Items.Add(new ComboBoxItem { Content = Label(draft.Name, draft.Endpoint, draft.Model) });
        _current = Math.Clamp(active, 0, _drafts.Count - 1);
        ProfileComboBox.SelectedIndex = _current;
        _loading = false;
        LoadFields();

        _initialLanguage = preferences.Language;
        SelectByTag(LanguageComboBox, _initialLanguage);

        _initialSandbox = preferences.SandboxFolder ?? string.Empty;
        SandboxTextBox.Text = _initialSandbox;
        CustomInstructionsTextBox.Text = preferences.CustomInstructions ?? string.Empty;
        SearchHistoryToggle.IsOn = preferences.SearchHistory;
        MemoryToggle.IsOn = preferences.MemoryEnabled;
        MemoryBudgetBox.Value = preferences.MemoryBudgetTokens;
        SelectByTag(MemoryScopeCombo, preferences.MemoryDefaultScope);
        MemoryArchiveBox.Value = preferences.MemoryArchiveDays;
        SelectByTag(MemoryRemindCombo, preferences.MemoryRemindAfter.ToString(CultureInfo.InvariantCulture));

        CurrencyTextBox.Text = preferences.Currency;

        UpdateSaveButtonState();
    }

    /// <summary>What the connection list shows: its name, or "model · host" until it has one.</summary>
    private static string Label(string name, string endpoint, string model)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        endpoint = endpoint.Trim();
        model = model.Trim();
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : endpoint;
        return (model.Length, host.Length) switch
        {
            (0, 0) => Strings.ProfileNew,
            (0, _) => host,
            (_, 0) => model,
            _ => $"{model} · {host}",
        };
    }

    private void LoadFields()
    {
        var draft = _drafts[_current];
        _loading = true;
        ProfileNameTextBox.Text = draft.Name;
        EndpointTextBox.Text = draft.Endpoint;
        ModelTextBox.Text = draft.Model;
        ImageModelTextBox.Text = draft.ImageModel;
        ApiKeyPasswordBox.Password = string.Empty;
        ApiKeyPasswordBox.PlaceholderText = draft.ApiKey.Length > 0 ? Strings.ApiKeyKeep : Strings.ApiKeyEnter;
        SelectByTag(ApiComboBox, draft.Api.ToString());

        _prices.TryGetValue(draft.Model, out var price);
        PriceInputBox.Value = price is null ? double.NaN : (double)price.Input;
        PriceCachedBox.Value = price is null ? double.NaN : (double)price.CachedInput;
        PriceOutputBox.Value = price is null ? double.NaN : (double)price.Output;

        ProfileDeleteButton.IsEnabled = _drafts.Count > 1;
        _loading = false;
        UpdateSaveButtonState();
    }

    /// <summary>Copies the boxes into the connection being shown (and its price into the price list).</summary>
    private void CommitFields()
    {
        var draft = _drafts[_current];
        draft.Name = ProfileNameTextBox.Text?.Trim() ?? string.Empty;
        draft.Endpoint = EndpointTextBox.Text?.Trim() ?? string.Empty;
        draft.Model = ModelTextBox.Text?.Trim() ?? string.Empty;
        draft.ImageModel = ImageModelTextBox.Text?.Trim() ?? string.Empty;
        draft.Api = ProviderApiExtensions.Parse(SelectedTag(ApiComboBox));
        if (!string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            draft.ApiKey = ApiKeyPasswordBox.Password;
        CommitPrice(draft.Model);
    }

    /// <summary>
    /// Sets this model's entry in the prices from the three boxes (removed when all are empty).
    /// An empty cached price means no cache discount: cached tokens cost the same as input.
    /// </summary>
    private void CommitPrice(string model)
    {
        if (model.Length == 0)
            return;
        _prices.Remove(model);

        static decimal? Read(NumberBox box) => double.IsNaN(box.Value) ? null : (decimal)Math.Max(0, box.Value);
        var input = Read(PriceInputBox);
        var cached = Read(PriceCachedBox);
        var output = Read(PriceOutputBox);
        if (input is not null || cached is not null || output is not null)
            _prices[model] = new Harness.Core.Usage.ModelPrice(input ?? 0, cached ?? input ?? 0, output ?? 0);
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileComboBox.SelectedIndex < 0)
            return;
        CommitFields();
        _current = ProfileComboBox.SelectedIndex;
        LoadFields();
    }

    private void ProfileAdd_Click(object sender, RoutedEventArgs e)
    {
        CommitFields();
        _drafts.Add(new Draft());
        _loading = true;
        ProfileComboBox.Items.Add(new ComboBoxItem { Content = Strings.ProfileNew });
        _current = _drafts.Count - 1;
        ProfileComboBox.SelectedIndex = _current;
        _loading = false;
        LoadFields();
        EndpointTextBox.Focus(FocusState.Programmatic);
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_drafts.Count <= 1)
            return;
        _loading = true;
        _drafts.RemoveAt(_current);
        ProfileComboBox.Items.RemoveAt(_current);
        _current = Math.Min(_current, _drafts.Count - 1);
        ProfileComboBox.SelectedIndex = _current;
        _loading = false;
        LoadFields();
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

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        if (ProfileComboBox.Items[_current] is ComboBoxItem item)
            item.Content = Label(ProfileNameTextBox.Text ?? string.Empty, EndpointTextBox.Text ?? string.Empty, ModelTextBox.Text ?? string.Empty);
        UpdateSaveButtonState();
    }

    private void UpdateSaveButtonState()
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(EndpointTextBox.Text);
        var hasModel = !string.IsNullOrWhiteSpace(ModelTextBox.Text);
        var hasApiKey = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password) || _drafts[_current].ApiKey.Length > 0;

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
        CommitFields();
        var shown = _drafts[_current];

        if (string.IsNullOrWhiteSpace(shown.Endpoint) || string.IsNullOrWhiteSpace(shown.Model))
        {
            args.Cancel = true;
            ShowError(Strings.EndpointModelRequired, ConnectionTab);
            return;
        }
        if (shown.ApiKey.Length == 0)
        {
            args.Cancel = true;
            ShowError(Strings.ApiKeyRequired, ConnectionTab);
            return;
        }

        // The others: one left completely empty (an Add that wasn't used) is dropped, one half filled in is an error.
        var profiles = new List<ProviderProfile>();
        var active = 0;
        for (var i = 0; i < _drafts.Count; i++)
        {
            var d = _drafts[i];
            if (i != _current && d.Endpoint.Length == 0 && d.Model.Length == 0 && d.ApiKey.Length == 0 && d.Name.Length == 0)
                continue;
            if (d.Endpoint.Length == 0 || d.Model.Length == 0 || d.ApiKey.Length == 0)
            {
                args.Cancel = true;
                ShowError(Strings.ProfileIncomplete(Label(d.Name, d.Endpoint, d.Model)), ConnectionTab);
                ProfileComboBox.SelectedIndex = i;
                return;
            }
            if (i == _current)
                active = profiles.Count;
            profiles.Add(new ProviderProfile(d.Name, d.Endpoint, d.Model, d.ApiKey, d.Api, d.ImageModel.Length == 0 ? null : d.ImageModel));
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
                ShowError(Strings.SandboxFailed(ex.Message), GeneralTab);
                return;
            }
        }

        var currency = (CurrencyTextBox.Text ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$"))
        {
            args.Cancel = true;
            ShowError(Strings.CurrencyInvalid, PricingTab);
            return;
        }

        try
        {
            LocalSettingsStore.Save(profiles, active);

            var language = SelectedTag(LanguageComboBox);
            LanguageChanged = !string.Equals(language, _initialLanguage, StringComparison.OrdinalIgnoreCase);
            var current = AppPreferences.Load();
            (current with
            {
                Language = language,
                SandboxFolder = sandboxChanged ? sandbox : current.SandboxFolder,
                Currency = currency,
                ModelPrices = _prices.Count == 0 ? null : _prices,
                CustomInstructions = string.IsNullOrWhiteSpace(CustomInstructionsTextBox.Text) ? null : CustomInstructionsTextBox.Text.Trim(),
                SearchHistory = SearchHistoryToggle.IsOn,
                MemoryEnabled = MemoryToggle.IsOn,
                MemoryBudgetTokens = double.IsNaN(MemoryBudgetBox.Value) ? 2000 : (int)Math.Clamp(MemoryBudgetBox.Value, 0, 8000),
                MemoryDefaultScope = SelectedTag(MemoryScopeCombo) is { Length: > 0 } scope ? scope : "global",
                MemoryArchiveDays = double.IsNaN(MemoryArchiveBox.Value) ? 90 : (int)Math.Clamp(MemoryArchiveBox.Value, 0, 3650),
                MemoryRemindAfter = int.TryParse(SelectedTag(MemoryRemindCombo), CultureInfo.InvariantCulture, out var remind) ? remind : 0,
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

        var chosen = profiles[active];
        Saved = new ResolvedProvider(chosen.Endpoint, chosen.Model, chosen.ApiKey, chosen.Api, ProviderSource.LocalEncryptedSettings, chosen.ImageModel);
    }

    // Positions of the pages in the Pivot (see SettingsDialog.xaml).
    private const int ConnectionTab = 0;
    private const int PricingTab = 1;
    private const int GeneralTab = 2;

    /// <summary>Shows the message under the pages, and switches to the page the problem is on.</summary>
    private void ShowError(string message, int? tab = null)
    {
        if (tab is { } index)
            SettingsPivot.SelectedIndex = index;
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
