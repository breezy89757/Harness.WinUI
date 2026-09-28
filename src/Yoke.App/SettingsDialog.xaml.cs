// Yoke — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Yoke.Core.Config;

namespace Yoke.App;

/// <summary>
/// App settings: UI language, model provider (endpoint, model, API key, API type, image model) and the
/// sandbox folder the built-in file tools may access. The API key is DPAPI-encrypted via
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

    public ResolvedProvider? Saved { get; private set; }

    public bool LanguageChanged { get; private set; }

    /// <summary>Set when the sandbox folder changed: the new folder, or empty when file tools were turned off.</summary>
    public string? NewSandboxFolder { get; private set; }

    public SettingsDialog(
        string? initialEndpoint, string? initialModel, ProviderApi initialApi, string? initialImageModel,
        bool hasExistingApiKey, nint windowHandle)
    {
        InitializeComponent();

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

        UpdateSaveButtonState();
    }

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

        try
        {
            LocalSettingsStore.Save(endpoint, model, apiKeyToSave, api, imageModel);

            var language = SelectedTag(LanguageComboBox);
            LanguageChanged = !string.Equals(language, _initialLanguage, StringComparison.OrdinalIgnoreCase);
            if (LanguageChanged || sandboxChanged)
                (AppPreferences.Load() with { Language = language, SandboxFolder = sandbox }).Save();
            if (sandboxChanged)
                NewSandboxFolder = sandbox;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowError(Strings.SaveFailed(ex.Message));
            return;
        }

        Saved = new ResolvedProvider(endpoint, model, apiKeyToSave, api, ProviderSource.LocalEncryptedSettings, imageModel);
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
