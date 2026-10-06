// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Harness.Core.Observability;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>
/// The Statistics view: for the chosen period, totals, then per model (latency percentiles, first
/// token, tokens, cost), per tool (how often it was offered vs. called, failures, time, result size),
/// per skill, per MCP server and per approval decision.
/// </summary>
public sealed partial class ObservabilityWindow
{
    private bool ShowingStats => ViewTabs.SelectedItem == StatsTab;

    private void ViewTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_initializing)
            return;
        StatsView.Visibility = ShowingStats ? Visibility.Visible : Visibility.Collapsed;
        LogView.Visibility = ShowingStats ? Visibility.Collapsed : Visibility.Visible;
        // Search and "errors only" filter the log; statistics cover the whole period.
        SearchBox.IsEnabled = FailedOnlyBox.IsEnabled = !ShowingStats;
        _ = RefreshAsync();
    }

    private async Task RefreshStatsAsync(TraceRecorder recorder, int days)
    {
        TraceStatistics stats;
        try
        {
            stats = await Task.Run(() => recorder.Store.Statistics(DateTimeOffset.UtcNow.AddDays(-days)));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            StatsPanel.Children.Clear();
            StatsPanel.Children.Add(Caption($"{Strings.TraceUnavailable} {ex.Message}"));
            return;
        }

        StatsPanel.Children.Clear();
        var totals = stats.Totals;
        if (totals.Turns == 0 && totals.ModelCalls == 0)
        {
            StatsPanel.Children.Add(Caption(recorder.IsRecording ? Strings.StatNone : Strings.TraceOffBody(Harness.Core.Config.AppPreferences.Load().TraceRetentionDays)));
            return;
        }

        var cards = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 184, ItemHeight = 84 };
        cards.Children.Add(Card(Strings.StatTurns, totals.Turns.ToString("N0", CultureInfo.CurrentCulture), Strings.StatTurnsTip));
        cards.Children.Add(Card(Strings.StatFailed, totals.FailedTurns.ToString("N0", CultureInfo.CurrentCulture), Strings.StatFailedTip, totals.FailedTurns > 0));
        cards.Children.Add(Card(Strings.StatModelCalls, totals.ModelCalls.ToString("N0", CultureInfo.CurrentCulture), Strings.StatModelCallsTip));
        cards.Children.Add(Card(Strings.StatToolCalls, totals.ToolCalls.ToString("N0", CultureInfo.CurrentCulture), Strings.StatToolCallsTip));
        cards.Children.Add(Card(Strings.StatTokens, $"{ChatMarkup.FormatTokens(totals.InputTokens)} / {ChatMarkup.FormatTokens(totals.OutputTokens)}", Strings.StatTokensTip));
        cards.Children.Add(Card(Strings.StatCacheHit, Percent(totals.CacheHitRate), Strings.StatCacheHitTip));
        if (totals.Cost is { } cost)
            cards.Children.Add(Card(Strings.StatCost, ChatMarkup.FormatCost((decimal)cost, totals.Currency ?? ""), Strings.StatCostTip));
        cards.Children.Add(Card(Strings.StatAverageTurn, FormatDuration(totals.AverageTurnMs), Strings.StatAverageTurnTip));
        StatsPanel.Children.Add(cards);

        if (stats.Models.Count > 0)
        {
            StatsSection(Strings.StatModels, Strings.StatModelsNote, Table(
                [Strings.ColModel, Strings.ColCalls, Strings.ColFailures, Strings.ColP50, Strings.ColP95, Strings.ColTtft, Strings.ColInput, Strings.ColCacheHit, Strings.ColOutput, Strings.ColCost, Strings.ColToolDefs],
                stats.Models.Select(m => new[]
                {
                    m.Model, N(m.Calls), N(m.Failures), FormatDuration(m.P50Ms), FormatDuration(m.P95Ms),
                    m.AverageTtftMs is { } t ? FormatDuration(t) : "—",
                    ChatMarkup.FormatTokens(m.InputTokens), Percent(m.CacheHitRate), ChatMarkup.FormatTokens(m.OutputTokens),
                    m.Cost is { } c ? ChatMarkup.FormatCost((decimal)c, totals.Currency ?? "") : "—",
                    m.AverageToolDefinitionsChars > 0 ? Strings.Chars(m.AverageToolDefinitionsChars.ToString("N0", CultureInfo.CurrentCulture)) : "—",
                })));
        }

        if (stats.Tools.Count > 0)
        {
            StatsSection(Strings.StatTools, Strings.StatToolsNote, Table(
                [Strings.ColTool, Strings.ColSource, Strings.ColOffered, Strings.ColCalls, Strings.ColUseRate, Strings.ColFailures, Strings.ColAverage, Strings.ColMax, Strings.ColResultSize],
                stats.Tools.Select(t => new[]
                {
                    t.Name, t.Source, N(t.Offered), N(t.Calls),
                    t.Offered > 0 ? (t.Calls / (double)t.Offered).ToString("P0", CultureInfo.CurrentCulture) : "—",
                    N(t.Failures), t.Calls > 0 ? FormatDuration(t.AverageMs) : "—", t.Calls > 0 ? FormatDuration(t.MaxMs) : "—",
                    t.AverageResultChars is { } size ? Strings.Chars(size.ToString("N0", CultureInfo.CurrentCulture)) : "—",
                }),
                dimRow: row => row[3] == "0"));
        }

        if (stats.Skills.Count > 0)
        {
            StatsSection(Strings.StatSkills, Strings.StatSkillsNote, Table(
                [Strings.ColSkill, Strings.ColLoads, Strings.ColFileReads, Strings.ColLastUsed],
                stats.Skills.Select(s => new[]
                {
                    s.Name, N(s.Loads), N(s.FileReads), s.LastUsed.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.CurrentCulture),
                })));
        }

        var servers = stats.Tools.Where(t => t.Source.StartsWith("mcp:", StringComparison.Ordinal) && t.Calls > 0)
            .GroupBy(t => t.Source[4..])
            .Select(g => new[]
            {
                g.Key, N(g.Sum(t => t.Calls)), N(g.Sum(t => t.Failures)),
                FormatDuration(g.Sum(t => t.AverageMs * t.Calls) / g.Sum(t => t.Calls)),
            })
            .ToList();
        if (servers.Count > 0)
            StatsSection(Strings.StatMcpServers, Strings.StatMcpServersNote, Table([Strings.ColServer, Strings.ColCalls, Strings.ColFailures, Strings.ColAverage], servers));

        if (stats.Approvals.Count > 0)
        {
            StatsSection(Strings.StatApprovals, Strings.StatApprovalsNote, Table(
                [Strings.ColDecision, Strings.ColCount, Strings.ColWait],
                stats.Approvals.Select(a => new[] { Strings.TraceDecision(a.Decision), N(a.Count), FormatDuration(a.AverageWaitMs) })));
        }
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A 0–1 share as a percentage, or a dash when the provider didn't report it.</summary>
    private static string Percent(double? share) => share is { } s ? s.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private void StatsSection(string title, string? note, UIElement table)
    {
        StatsPanel.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 12, 0, 0),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        if (note is not null)
            StatsPanel.Children.Add(Caption(note));
        StatsPanel.Children.Add(new ScrollViewer
        {
            Content = table,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        });
    }

    private static Border Card(string label, string value, string tip, bool alert = false)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Caption(label));
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            Foreground = alert ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var card = new Border
        {
            Child = panel,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        ToolTipService.SetToolTip(card, tip);
        return card;
    }

    /// <summary>
    /// A table in a card: a header row over a divider, then rows with alternating shading and a line
    /// between them. The first column is a name; the rest are right-aligned figures.
    /// </summary>
    private static Border Table(string[] headers, IEnumerable<string[]> rows, Func<string[], bool>? dimRow = null)
    {
        var grid = new Grid();
        foreach (var _ in headers)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resources = Application.Current.Resources;

        void AddRow(string[] cells, bool header, bool shaded, bool dim)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;

            // Row background and the line under it, across every column.
            var band = new Border
            {
                Background = shaded ? (Brush)resources["SubtleFillColorSecondaryBrush"] : null,
                BorderBrush = (Brush)resources[header ? "ControlStrongStrokeColorDefaultBrush" : "DividerStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Grid.SetRow(band, row);
            Grid.SetColumnSpan(band, headers.Length);
            grid.Children.Add(band);

            for (var i = 0; i < cells.Length && i < headers.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = cells[i],
                    Padding = new Thickness(i == 0 ? 16 : 20, header ? 10 : 8, i == headers.Length - 1 ? 16 : 0, header ? 10 : 8),
                    HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = (Brush)resources[header || dim ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"],
                    IsTextSelectionEnabled = !header,
                };
                if (header)
                    text.Style = (Style)resources["CaptionTextBlockStyle"];
                Grid.SetRow(text, row);
                Grid.SetColumn(text, i);
                grid.Children.Add(text);
            }
        }

        AddRow(headers, header: true, shaded: false, dim: false);
        var index = 0;
        foreach (var cells in rows)
            AddRow(cells, header: false, shaded: index++ % 2 == 1, dim: dimRow?.Invoke(cells) == true);

        return new Border
        {
            Child = grid,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)resources["CardBackgroundFillColorDefaultBrush"],
        };
    }
}
