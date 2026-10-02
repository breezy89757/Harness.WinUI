// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>The usage button's flyout: tokens and cost for this conversation, today, this month and all time.</summary>
public sealed partial class MainWindow
{
    private async void UsageFlyout_Opening(object sender, object e)
    {
        UsageGrid.Children.Clear();
        UsageGrid.RowDefinitions.Clear();
        UsageGrid.ColumnDefinitions.Clear();
        UsageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UsageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var report = await ViewModel.UsageReportAsync();
        if (report.Count == 0 || report.All(r => r.Totals.Replies == 0))
        {
            AddUsageRow(0, report.Count == 0 ? Strings.UsageUnavailable : Strings.UsageNone, null, null);
            return;
        }

        for (var i = 0; i < report.Count; i++)
        {
            var (label, totals) = report[i];
            var amount = totals.Replies == 0 ? "—" : ChatMarkup.FormatTotalsShort(totals);
            var details = totals.Replies == 0 ? null : string.Join(" · ", new[]
            {
                Strings.UsageReplies(totals.Replies),
                Strings.UsageTokens(ChatMarkup.FormatTokens(totals.InputTokens), ChatMarkup.FormatTokens(totals.OutputTokens)),
                totals.UnpricedReplies > 0 && totals.Cost.Count > 0 ? Strings.UsageUnpriced(totals.UnpricedReplies) : null,
            }.Where(s => s is not null));
            AddUsageRow(i, label, amount, details);
        }
    }

    private void AddUsageRow(int row, string label, string? amount, string? details)
    {
        UsageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        Grid.SetRow(name, row);
        UsageGrid.Children.Add(name);

        if (amount is null)
            return;

        var value = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        value.Children.Add(new TextBlock { Text = amount, HorizontalAlignment = HorizontalAlignment.Right, IsTextSelectionEnabled = true });
        if (details is not null)
        {
            value.Children.Add(new TextBlock
            {
                Text = details,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        UsageGrid.Children.Add(value);
    }
}
