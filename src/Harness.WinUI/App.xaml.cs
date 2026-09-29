// Harness.WinUI — Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.UI.Xaml;
using Harness.Core.Agent;
using Harness.Core.Config;
using Harness.Core.Providers;
using Harness.Core.Tools;

namespace Harness.WinUI;

public partial class App : Application
{
    private Window? _window;

    // Configured and started by MainWindow once the chat shell is up; sessions read its tools on every turn.
    private readonly AgentTools _tools = new();

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (PackagedDataFolder() is { } packaged)
            AppPaths.UseDataRoot(packaged);
        else
            AppPaths.MigrateLegacyDataRoot();

        var preferences = AppPreferences.Load();
        Strings.Initialize(preferences.Language);

        // First run with built-in file tools: adopt the folder the old Node filesystem MCP server used
        // (and drop that server), otherwise the default sandbox.
        if (preferences.SandboxFolder is null)
        {
            preferences = preferences with
            {
                SandboxFolder = McpServerManager.TakeOverFilesystemServer() ?? AppPreferences.DefaultSandboxFolder,
            };
            preferences.Save();
        }
        _tools.SetSandbox(preferences.SandboxFolder);

        var (chatSession, options, startupError, needsProviderSetup) = BuildChatSession();

        _window = new MainWindow(chatSession, options, startupError, needsProviderSetup, _tools);
        _window.Activate();
    }

    /// <summary>
    /// The package's LocalState folder when running as an MSIX package (Microsoft Store), else null.
    /// See <see cref="AppPaths"/> for why packaged data doesn't go to %LOCALAPPDATA%.
    /// </summary>
    private static string? PackagedDataFolder()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current; // throws when the app has no package identity
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Image generation reuses the chat provider's endpoint and key; no image model means no tool.</summary>
    public static ImageGenerationSettings? ImageSettingsFor(ResolvedProvider provider) =>
        provider.ImageModel is { } model ? new ImageGenerationSettings(provider.Endpoint, provider.ApiKey, model) : null;

    /// <summary>
    /// Loads HarnessOptions the same way the dev console does (appsettings.json required,
    /// appsettings.local.json optional overlay, then HARNESS_-prefixed environment variables), then
    /// resolves the provider via <see cref="ProviderResolver"/> — which prefers whatever the user
    /// saved through the in-app Settings dialog (<see cref="LocalSettingsStore"/>, key encrypted
    /// with DPAPI) and falls back to the config-file/env-var path for local dev and CI.
    ///
    /// Three outcomes:
    ///  - Resolved + client builds cleanly: chat session ready, straight to the chat view.
    ///  - Resolved but the client fails to build (e.g. a bad endpoint URI): startupError is set so
    ///    MainWindow can surface it as a system bubble, same as before.
    ///  - Nothing resolved anywhere: needsProviderSetup is set so MainWindow shows the Settings
    ///    dialog automatically once the shell has loaded, instead of failing silently.
    /// </summary>
    private (ChatSession? Session, HarnessOptions Options, string? StartupError, bool NeedsProviderSetup) BuildChatSession()
    {
        IConfiguration config;
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.local.json", optional: true)
                .AddEnvironmentVariables(prefix: "HARNESS_")
                .Build();
        }
        catch (Exception ex)
        {
            return (null, new HarnessOptions(), Strings.ConfigLoadFailed(ex.Message), false);
        }

        var options = config.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>() ?? new HarnessOptions();

        var resolved = ProviderResolver.TryResolve(options.Provider);
        if (resolved is null)
        {
            // Neither the encrypted local settings nor appsettings.json/env vars have a usable
            // config — prompt the user to configure one instead of opening "not connected".
            return (null, options, null, true);
        }

        try
        {
            var session = new ChatSession(
                ChatClientFactory.Create(resolved), options, () => _tools.All, ChatClientFactory.SupportsReasoningSummaries(resolved));
            _tools.SetImageGeneration(ImageSettingsFor(resolved));
            return (session, options, null, false);
        }
        catch (ProviderConfigurationException ex)
        {
            return (null, options,
                Strings.ProviderConnectFailed(resolved.Model, resolved.Endpoint, ex.Message),
                false);
        }
        catch (Exception ex)
        {
            return (null, options, Strings.ProviderStartFailed(ex.Message), false);
        }
    }
}
