// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>The helper agents (Code mode's spawn_agent) as small cards above the message box: what each is asked, what it is doing now, how long it has worked.</summary>
public sealed partial class MainWindow
{
    private sealed class HelperCard(string callId, string task)
    {
        public string CallId { get; } = callId;
        public string Task { get; } = task;
        public DateTime Started { get; } = DateTime.UtcNow;
        public TimeSpan? Final { get; set; }
        public int Steps { get; set; }
        public string Stopped { get; set; } = string.Empty;
        public bool Finished => Final is not null;

        public ProgressRing Ring { get; } = new() { Width = 16, Height = 16, IsActive = true };
        public FontIcon Done { get; } = new() { Glyph = "", FontSize = 14, Visibility = Visibility.Collapsed, Foreground = Brush("SystemFillColorSuccessBrush") };
        public TextBlock Latest { get; } = new()
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };
        public ProgressBar Bar { get; } = new() { IsIndeterminate = true, Height = 3 };
        public TextBlock Time { get; } = new() { Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        public Button Stop { get; } = new();
        public Border Root { get; } = new();
    }

    private readonly Dictionary<string, HelperCard> _helperCards = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _helperTimer;
    private DateTime _helpersIdleSince = DateTime.UtcNow;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void ConfigureHelpers()
    {
        _tools.HelperProgress += e => DispatcherQueue.TryEnqueue(() =>
        {
            _ = ViewModel.ShowHelperProgressAsync(e);
            try
            {
                ShowHelper(e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The cards are a convenience; the step in the reply still shows the helper.
                Diagnostics.Log($"helper card failed: {ex}");
            }
        });
    }

    private void ShowHelper(HelperEvent e)
    {
        if (!_helperCards.TryGetValue(e.CallId, out var card))
        {
            if (e.Kind != HelperEventKind.Started)
                return;
            card = NewHelperCard(e.CallId, e.Text);
            _helperCards[e.CallId] = card;
            HelperCardsPanel.Children.Add(card.Root);
        }

        card.Steps = e.Steps;
        switch (e.Kind)
        {
            case HelperEventKind.Step:
                card.Latest.Text = e.Text;
                break;
            case HelperEventKind.Finished:
                card.Final = e.Elapsed;
                card.Time.Text = $"{(int)e.Elapsed.TotalMinutes}:{e.Elapsed.Seconds:00}";
                _helpersIdleSince = DateTime.UtcNow;
                card.Stopped = e.Text;
                card.Ring.Visibility = Visibility.Collapsed;
                card.Done.Visibility = Visibility.Visible;
                card.Bar.Visibility = Visibility.Collapsed;
                card.Stop.Visibility = Visibility.Collapsed;
                card.Latest.Text = Strings.HelperSummary(e.Steps, e.Elapsed) + (e.Text.Length > 0 ? " " + Strings.HelperWhy(e.Text) : string.Empty);
                break;
        }

        RefreshHelperHeader();
        if (_helperTimer is null)
        {
            _helperTimer = DispatcherQueue.CreateTimer();
            _helperTimer.Interval = TimeSpan.FromSeconds(1);
            _helperTimer.Tick += (_, _) => TickHelpers();
        }
        if (!_helperTimer.IsRunning)
            _helperTimer.Start();
    }

    private HelperCard NewHelperCard(string callId, string task)
    {
        var card = new HelperCard(callId, task);
        var title = new TextBlock
        {
            Text = FirstLine(task),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(title, task.Length > 600 ? task[..600] + "…" : task);

        card.Stop.Content = new FontIcon { Glyph = "", FontSize = 10 };
        card.Stop.Padding = new Thickness(6, 4, 6, 4);
        card.Stop.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        card.Stop.BorderThickness = new Thickness(0);
        ToolTipService.SetToolTip(card.Stop, Strings.HelperStopTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card.Stop, Strings.HelperStopTooltip);
        card.Stop.Click += (_, _) => _tools.Helpers.Stop(callId);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(card.Latest);
        text.Children.Add(card.Bar);

        var status = new Grid { Width = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        status.Children.Add(card.Ring);
        status.Children.Add(card.Done);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(card.Time, 2);
        grid.Children.Add(card.Time);
        Grid.SetColumn(card.Stop, 3);
        grid.Children.Add(card.Stop);

        card.Root.Child = grid;
        card.Root.Padding = new Thickness(10, 8, 6, 8);
        card.Root.CornerRadius = new CornerRadius(6);
        card.Root.Background = Brush("CardBackgroundFillColorDefaultBrush");
        card.Root.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        card.Root.BorderThickness = new Thickness(1);
        card.Time.Text = "0:00";
        return card;
    }

    /// <summary>The first sentence or line of the question, short enough for a card's title.</summary>
    private static string FirstLine(string task)
    {
        var line = task.ReplaceLineEndings(" ").Trim();
        var end = line.IndexOfAny(['.', '。', '?', '？', '!']);
        if (end is > 20 and < 140)
            line = line[..(end + 1)];
        return line.Length > 140 ? line[..137] + "…" : line;
    }

    private void RefreshHelperHeader()
    {
        var running = _helperCards.Values.Count(c => !c.Finished);
        HelperPanel.Visibility = _helperCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HelperHeaderText.Text = Strings.HelperPanelHeader(running, _helperCards.Count);
        if (running > 0)
            _helpersIdleSince = DateTime.UtcNow;
    }

    // Once a second: the clocks of the cards that are working; a few seconds after the last one is done (or the reply was stopped) the panel goes away.
    private void TickHelpers()
    {
        foreach (var card in _helperCards.Values.Where(c => !c.Finished))
        {
            var elapsed = DateTime.UtcNow - card.Started;
            card.Time.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        }

        if (_helperCards.Values.Any(c => !c.Finished))
            return;
        if ((DateTime.UtcNow - _helpersIdleSince).TotalSeconds < 6)
            return;

        _helperTimer?.Stop();
        _helperCards.Clear();
        HelperCardsPanel.Children.Clear();
        HelperPanel.Visibility = Visibility.Collapsed;
    }
}
