// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Harness.Core.Observability;

namespace Harness.WinUI;

/// <summary>AI analysis on the Statistics view: the configured model answers questions about the selected period's records.</summary>
public sealed partial class ObservabilityWindow
{
    private CancellationTokenSource? _analysis;

    private void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is not null)
            _analysis.Cancel();
        else if (!string.IsNullOrWhiteSpace(AskBox.Text))
            _ = AnalyzeAsync(AskBox.Text.Trim());
    }

    private void OverviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null)
            _ = AnalyzeAsync(Strings.AnalysisOverviewQuestion);
    }

    private void AskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _analysis is null && !string.IsNullOrWhiteSpace(AskBox.Text))
        {
            e.Handled = true;
            _ = AnalyzeAsync(AskBox.Text.Trim());
        }
    }

    private async Task AnalyzeAsync(string question)
    {
        AnswerPanel.Visibility = Visibility.Visible;
        if (_recorder is not { } recorder)
        {
            AnswerText.Text = Strings.TraceUnavailable;
            return;
        }

        Microsoft.Extensions.AI.IChatClient? client;
        try
        {
            client = _analysisClient();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnswerText.Text = Strings.AnalysisFailed(ex.Message);
            return;
        }
        if (client is null)
        {
            AnswerText.Text = Strings.AnalysisNoModel;
            return;
        }

        var days = int.Parse((string)((Microsoft.UI.Xaml.Controls.ComboBoxItem)RangeBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        using var cancellation = new CancellationTokenSource();
        _analysis = cancellation;
        AskButton.Content = Strings.AnalysisStop;
        OverviewButton.IsEnabled = false;
        AnswerProgress.Visibility = Visibility.Visible;
        AnswerText.Text = string.Empty;

        var answer = new StringBuilder();
        try
        {
            var lastShown = DateTime.MinValue;
            await foreach (var text in new TraceAnalyst(recorder.Store).AskAsync(client, question, days, cancellation.Token))
            {
                answer.Append(text);
                if ((DateTime.UtcNow - lastShown).TotalMilliseconds > 80)
                {
                    lastShown = DateTime.UtcNow;
                    AnswerText.Text = answer.ToString();
                }
            }
            AnswerText.Text = answer.ToString();
        }
        catch (OperationCanceledException)
        {
            AnswerText.Text = answer.Length > 0 ? answer + $"\n\n({Strings.Stopped})" : Strings.Stopped;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnswerText.Text = (answer.Length > 0 ? answer + "\n\n" : "") + Strings.AnalysisFailed(ex.Message);
        }
        finally
        {
            client.Dispose();
            _analysis = null;
            AskButton.Content = Strings.AnalysisAsk;
            OverviewButton.IsEnabled = true;
            AnswerProgress.Visibility = Visibility.Collapsed;
        }
    }
}
