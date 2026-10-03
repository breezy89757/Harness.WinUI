// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Harness.WinUI;

/// <summary>Opens the observability window (one at a time), from the toolbar or a reply's footer.</summary>
public sealed partial class MainWindow
{
    private ObservabilityWindow? _observability;

    private void ObservabilityButton_Click(object sender, RoutedEventArgs e) => OpenObservability(null);

    /// <param name="traceId">A recorded turn to show, or null for the list.</param>
    private void OpenObservability(string? traceId)
    {
        if (_observability is null)
        {
            // AI analysis uses the configured model, without recording those calls.
            _observability = new ObservabilityWindow(App.Traces, () =>
                Harness.Core.Config.ProviderResolver.TryResolve(_options.Provider) is { } provider
                    ? Harness.Core.Providers.ChatClientFactory.Create(provider, instrument: false)
                    : null);
            _observability.Closed += (_, _) => _observability = null;
            Closed += (_, _) => _observability?.Close();
        }
        if (_observability.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _observability.Activate();
        _observability.ShowTrace(traceId);
    }
}
