// Harness.WinUI — Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Harness.Core.Config;
using Harness.Core.Observability;
using Harness.WinUI.ViewModels;

namespace Harness.WinUI;

/// <summary>
/// What Harness.WinUI sent to the model and what came back, turn by turn, from the local trace database:
/// a list of turns; for the selected one, a timeline of its model calls, tool calls and approval waits;
/// for the selected step, its content (as the model saw it), the raw HTTP request and response, and every
/// recorded attribute. Recording is off until the user turns it on here.
/// </summary>
public sealed partial class ObservabilityWindow : Window
{
    private readonly TraceRecorder? _recorder;
    private readonly ObservableCollection<TurnItem> _turns = [];
    private readonly ObservableCollection<SpanItem> _spans = [];
    private readonly DispatcherQueueTimer _searchDelay;
    private IReadOnlyList<SpanRecord> _trace = [];
    private string? _wantedTraceId;
    private bool _initializing = true;

    public ObservabilityWindow(TraceRecorder? recorder)
    {
        InitializeComponent();
        _recorder = recorder;
        Title = $"{Strings.ObservabilityTitle} — Harness.WinUI";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Harness.ico"));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(820 * scale)));

        TurnList.ItemsSource = _turns;
        SpanList.ItemsSource = _spans;
        RangeBox.SelectedIndex = 1;
        RecordToggle.IsOn = recorder?.IsRecording == true;
        RecordToggle.IsEnabled = recorder is not null;

        _searchDelay = DispatcherQueue.CreateTimer();
        _searchDelay.Interval = TimeSpan.FromMilliseconds(300);
        _searchDelay.IsRepeating = false;
        _searchDelay.Tick += (_, _) => _ = RefreshAsync();

        if (recorder is not null)
        {
            recorder.TurnRecorded += OnTurnRecorded;
            Closed += (_, _) => recorder.TurnRecorded -= OnTurnRecorded;
        }
        ShowDetailPlaceholder();
        _initializing = false;
        _ = RefreshAsync();
    }

    /// <summary>Shows one turn (e.g. from a reply's footer): selected in the list, or loaded directly if it's outside the filters.</summary>
    public void ShowTrace(string? traceId)
    {
        if (string.IsNullOrEmpty(traceId))
            return;
        _wantedTraceId = traceId;
        if (_turns.FirstOrDefault(t => t.TraceId == traceId) is { } item)
        {
            TurnList.SelectedItem = item;
            TurnList.ScrollIntoView(item);
        }
        else
            _ = LoadTraceAsync(traceId);
    }

    #region Turn list

    private void OnTurnRecorded(object? sender, string traceId) => DispatcherQueue.TryEnqueue(() => _ = RefreshAsync());

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
            _ = RefreshAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_recorder is not { } recorder)
        {
            ShowEmpty(Strings.TraceUnavailable, string.Empty);
            return;
        }

        var days = int.Parse((string)((ComboBoxItem)RangeBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        var search = SearchBox.Text;
        var failedOnly = FailedOnlyBox.IsChecked == true;
        IReadOnlyList<TurnSummary> turns;
        try
        {
            turns = await Task.Run(() => recorder.Store.Turns(DateTimeOffset.UtcNow.AddDays(-days), search, failedOnly));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowEmpty(Strings.TraceUnavailable, ex.Message);
            return;
        }

        var keep = (TurnList.SelectedItem as TurnItem)?.TraceId ?? _wantedTraceId;
        _turns.Clear();
        foreach (var turn in turns)
            _turns.Add(TurnItem.From(turn));

        if (_turns.Count == 0)
        {
            if (!recorder.IsRecording)
                ShowEmpty(Strings.TraceOffTitle, Strings.TraceOffBody(AppPreferences.Load().TraceRetentionDays));
            else
                ShowEmpty(Strings.TraceEmpty, string.Empty);
        }
        else
            EmptyPanel.Visibility = Visibility.Collapsed;

        if (keep is not null && _turns.FirstOrDefault(t => t.TraceId == keep) is { } selected)
        {
            TurnList.SelectedItem = selected;
            TurnList.ScrollIntoView(selected);
        }
    }

    private void ShowEmpty(string title, string body)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void TurnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TurnList.SelectedItem is TurnItem item && item.TraceId != _trace.FirstOrDefault()?.TraceId)
            _ = LoadTraceAsync(item.TraceId);
    }

    #endregion

    #region Timeline

    private async Task LoadTraceAsync(string traceId)
    {
        if (_recorder is not { } recorder)
            return;
        try
        {
            _trace = await Task.Run(() => recorder.Store.Trace(traceId));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or JsonException)
        {
            _trace = [];
        }
        _wantedTraceId = null;

        _spans.Clear();
        var root = _trace.FirstOrDefault(s => s.Operation == Telemetry.OpTurn);
        if (root is null)
        {
            ShowDetailPlaceholder();
            return;
        }

        TurnHeader.Text = root.Content?.GetValueOrDefault(Telemetry.TagUserMessage) ?? Strings.TraceTurn;
        var byId = _trace.ToDictionary(s => s.SpanId);
        foreach (var span in _trace.Where(IsShown))
            _spans.Add(SpanItem.From(span, root, Depth(span, byId)));
        SpanList.SelectedIndex = Math.Min(1, _spans.Count - 1);
    }

    // The agent loop span and the raw HTTP spans are shown inside their parents, not as rows.
    private static bool IsShown(SpanRecord span) => span.Operation is not (Telemetry.OpAgentLoop or Telemetry.OpHttp);

    private static int Depth(SpanRecord span, Dictionary<string, SpanRecord> byId)
    {
        var depth = 0;
        for (var parent = span.ParentId; parent is not null && byId.TryGetValue(parent, out var p); parent = p.ParentId)
        {
            if (IsShown(p))
                depth++;
        }
        return depth;
    }

    private void SpanList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderDetail();

    private void DetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => RenderDetail();

    #endregion

    #region Step detail

    private void ShowDetailPlaceholder()
    {
        TurnHeader.Text = string.Empty;
        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(Caption(Strings.TraceSelectTurn));
        RawBox.Visibility = Visibility.Collapsed;
        DetailScroller.Visibility = Visibility.Visible;
    }

    private void RenderDetail()
    {
        if (SpanList.SelectedItem is not SpanItem { Span: var span })
            return;

        var tab = DetailTabs.SelectedItem;
        if (tab == RequestTab || tab == ResponseTab)
        {
            var http = _trace.LastOrDefault(s => s.Operation == Telemetry.OpHttp && s.ParentId == span.SpanId);
            var body = http?.Content?.GetValueOrDefault(tab == RequestTab ? Telemetry.TagHttpRequestBody : Telemetry.TagHttpResponseBody);
            RawBox.Text = body is null ? Strings.TraceNoRaw : PrettyJson(body) ?? body;
            RawBox.Visibility = Visibility.Visible;
            DetailScroller.Visibility = Visibility.Collapsed;
            return;
        }

        RawBox.Visibility = Visibility.Collapsed;
        DetailScroller.Visibility = Visibility.Visible;
        DetailPanel.Children.Clear();
        DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (tab == DetailsTab)
            RenderAttributes(span);
        else
            RenderContent(span);
    }

    private void RenderContent(SpanRecord span)
    {
        var content = span.Content ?? new Dictionary<string, string>();
        switch (span.Operation)
        {
            case Telemetry.OpTurn:
                AddSection(Strings.TraceUserMessage, content.GetValueOrDefault(Telemetry.TagUserMessage));
                AddSection(Strings.TraceReply, content.GetValueOrDefault(Telemetry.TagReply));
                break;

            case Telemetry.OpChat:
                // The same on every call and long: collapsed, like the tool list.
                if (content.GetValueOrDefault(Telemetry.TagSystemInstructions) is { } system)
                    AddExpander(Strings.TraceSystem, Body(PartsText(Parse(system))), expanded: false);
                if (Parse(content.GetValueOrDefault(Telemetry.TagToolDefinitions)) is JsonArray tools)
                {
                    var lines = tools.Select(t => $"{t?["name"]}  —  {FirstLine(t?["description"]?.ToString())}");
                    AddExpander(Strings.TraceToolsOffered(tools.Count), Body(string.Join("\n", lines)), expanded: false);
                }
                if (Parse(content.GetValueOrDefault(Telemetry.TagInputMessages)) is JsonArray input)
                {
                    // The newest messages are what this call is about; older history starts collapsed.
                    var panel = new StackPanel { Spacing = 8 };
                    var older = new StackPanel { Spacing = 8 };
                    for (var i = 0; i < input.Count; i++)
                        (i < input.Count - 3 ? older : panel).Children.Add(MessageCard(input[i]));
                    if (older.Children.Count > 0)
                        panel.Children.Insert(0, new Expander { Header = Strings.TraceInput(older.Children.Count), Content = older, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    AddSection(Strings.TraceInput(input.Count), panel);
                }
                if (Parse(content.GetValueOrDefault(Telemetry.TagOutputMessages)) is JsonArray output)
                {
                    var panel = new StackPanel { Spacing = 8 };
                    foreach (var message in output)
                        panel.Children.Add(MessageCard(message));
                    AddSection(Strings.TraceOutput, panel);
                }
                break;

            case Telemetry.OpTool:
                AddSection(Strings.TraceArguments, Mono(PrettyJson(content.GetValueOrDefault(Telemetry.TagToolArguments)) ?? content.GetValueOrDefault(Telemetry.TagToolArguments)));
                AddSection(Strings.TraceResult, Mono(ToolResultText(content.GetValueOrDefault(Telemetry.TagToolResult))));
                break;

            case Telemetry.OpApproval:
                DetailPanel.Children.Add(Body($"{Strings.TraceApproval(Strings.TraceDecision(span.Attributes.GetValueOrDefault(Telemetry.TagDecision)))} · {FormatDuration(span.DurationMs)}"));
                break;
        }

        if (span.Failed && span.Status is { Length: > 0 } status && status != Telemetry.StatusCancelled)
            DetailPanel.Children.Insert(0, new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = false, Title = Strings.TraceFailed, Message = status });
        if (DetailPanel.Children.Count == 0)
            DetailPanel.Children.Add(Caption(Strings.TraceNoContent));
    }

    private void RenderAttributes(SpanRecord span)
    {
        var rows = new List<(string, string?)>
        {
            ("name", span.Name),
            ("start", span.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture)),
            ("duration", FormatDuration(span.DurationMs)),
            ("status", span.Failed ? span.Status ?? "error" : "ok"),
            ("model", span.Model),
            ("tokens", span.InputTokens is null ? null : $"in {span.InputTokens:N0} (cached {span.CachedTokens ?? 0:N0}) · out {span.OutputTokens:N0} (reasoning {span.ReasoningTokens ?? 0:N0})"),
            ("time to first token", span.TimeToFirstChunkMs is { } ttft ? FormatDuration(ttft) : null),
            ("cost", span.Cost is { } cost ? ChatMarkup.FormatCost((decimal)cost, span.Currency ?? "") : null),
            ("tool source", span.ToolSource),
            ("trace id", span.TraceId),
            ("span id", span.SpanId),
        };
        foreach (var (key, value) in span.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
            rows.Add((key, value));

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (key, value) in rows.Where(r => !string.IsNullOrEmpty(r.Item2)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Caption(key);
            var text = Body(value!);
            Grid.SetRow(name, grid.RowDefinitions.Count - 1);
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
        }
        DetailPanel.Children.Add(grid);
    }

    /// <summary>A message in the GenAI convention's shape: a role and its parts (text, tool calls and responses, reasoning…).</summary>
    private static Border MessageCard(JsonNode? message)
    {
        var panel = new StackPanel { Spacing = 6 };
        var role = message?["role"]?.ToString() ?? "?";
        var name = message?["name"]?.ToString();
        panel.Children.Add(new TextBlock
        {
            Text = name is null ? role.ToUpperInvariant() : $"{role.ToUpperInvariant()} · {name}",
            FontWeight = FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        foreach (var part in message?["parts"] as JsonArray ?? [])
        {
            switch (part?["type"]?.ToString())
            {
                case "text":
                    panel.Children.Add(Body(part["content"]?.ToString()));
                    break;
                case "reasoning":
                    panel.Children.Add(Body(part["content"]?.ToString(), italic: true));
                    break;
                case "tool_call":
                    panel.Children.Add(Mono($"→ {part["name"]}({Compact(part["arguments"])})  #{part["id"]}"));
                    break;
                case "tool_call_response":
                    panel.Children.Add(Mono($"← #{part["id"]}  {Compact(part["response"] ?? part["result"])}"));
                    break;
                default:
                    panel.Children.Add(Mono(Compact(part)));
                    break;
            }
        }
        return new Border
        {
            Child = panel,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources[role == "assistant" ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"],
        };
    }

    private void AddSection(string header, string? text) => AddSection(header, text is null ? null : Body(text));

    private void AddSection(string header, UIElement? body)
    {
        if (body is null)
            return;
        DetailPanel.Children.Add(new TextBlock { Text = header, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        DetailPanel.Children.Add(body);
    }

    private void AddExpander(string header, UIElement body, bool expanded) =>
        DetailPanel.Children.Add(new Expander
        {
            Header = header,
            Content = body,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

    private const int MaxShownChars = 20_000;

    private static TextBlock Body(string? text, bool italic = false) => new()
    {
        Text = Clip(text),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
    };

    private static TextBlock Mono(string? text) => new()
    {
        Text = Clip(text),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static string Clip(string? text) =>
        text is null ? string.Empty : text.Length <= MaxShownChars ? text : text[..MaxShownChars] + $"\n…(+{text.Length - MaxShownChars:N0})";

    private static string? FirstLine(string? text) => text?.Split('\n', 2)[0];

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PartsText(JsonNode? parts) =>
        parts is JsonArray array ? string.Join("\n\n", array.Select(p => p?["content"]?.ToString() ?? p?.ToJsonString())) : parts?.ToString();

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions s_compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Indented JSON (with Chinese etc. left readable); null when the text isn't JSON (e.g. streamed events).</summary>
    private static string? PrettyJson(string? text) =>
        Parse(text) is { } node && text!.TrimStart() is ['{' or '[', ..] ? node.ToJsonString(s_indented) : null;

    private static string Compact(JsonNode? node)
    {
        var text = node?.ToJsonString(s_compact) ?? string.Empty;
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    /// <summary>Tool results are recorded as JSON; a plain string result reads better unquoted.</summary>
    private static string? ToolResultText(string? result) => Parse(result) switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => PrettyJson(text) ?? text,
        { } node => node.ToJsonString(s_indented),
        null => result,
    };

    internal static string FormatDuration(double ms) =>
        ms < 1000 ? $"{ms:N0} ms" : $"{ms / 1000:N1} s";

    #endregion

    #region Recording settings

    private void RecordToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _recorder is null || RecordToggle.IsOn == _recorder.IsRecording)
            return;
        var preferences = AppPreferences.Load() with { TraceEnabled = RecordToggle.IsOn };
        preferences.Save();
        _recorder.Configure(preferences.TraceEnabled, preferences.TraceContent, preferences.TraceRetentionDays);
        _ = RefreshAsync();
    }

    private bool _settingsLoading;

    private void SettingsFlyout_Opening(object sender, object e)
    {
        _settingsLoading = true;
        var preferences = AppPreferences.Load();
        CaptureContentBox.IsChecked = preferences.TraceContent;
        RetentionBox.Value = preferences.TraceRetentionDays;
        _settingsLoading = false;
    }

    private void CaptureContent_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void RetentionBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
            SaveSettings();
    }

    private void SaveSettings()
    {
        if (_settingsLoading || _recorder is null)
            return;
        var preferences = AppPreferences.Load() with
        {
            TraceContent = CaptureContentBox.IsChecked == true,
            TraceRetentionDays = double.IsNaN(RetentionBox.Value) ? 30 : (int)Math.Clamp(RetentionBox.Value, 1, 365),
        };
        preferences.Save();
        _recorder.Configure(preferences.TraceEnabled, preferences.TraceContent, preferences.TraceRetentionDays);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Hide();
        if (_recorder is not { } recorder)
            return;
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.TraceClear,
            Content = Strings.TraceClearConfirm,
            PrimaryButtonText = Strings.Delete,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;
        await Task.Run(recorder.Store.Clear);
        _trace = [];
        _spans.Clear();
        ShowDetailPlaceholder();
        await RefreshAsync();
    }

    #endregion

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}

/// <summary>A turn in the list.</summary>
public sealed class TurnItem
{
    public required string TraceId { get; init; }
    public required string Title { get; init; }
    public required string Caption { get; init; }
    public required string Tooltip { get; init; }
    public Visibility FailedVisibility { get; init; }

    public static TurnItem From(TurnSummary turn)
    {
        var parts = new List<string> { turn.StartedAt.ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.CurrentCulture) };
        if (turn.Model is { } model)
            parts.Add(model);
        parts.Add(Strings.TraceModelCalls(turn.ModelCalls));
        if (turn.ToolCalls > 0)
            parts.Add(Strings.TraceToolCalls(turn.ToolCalls));
        if (turn.InputTokens + turn.OutputTokens > 0)
            parts.Add(Strings.TraceTokens($"{ChatMarkup.FormatTokens(turn.InputTokens)}→{ChatMarkup.FormatTokens(turn.OutputTokens)}"));
        if (turn.Cost is { } cost)
            parts.Add(ChatMarkup.FormatCost((decimal)cost, turn.Currency ?? ""));
        parts.Add(ObservabilityWindow.FormatDuration(turn.DurationMs));

        var title = string.IsNullOrWhiteSpace(turn.UserMessage) ? "…" : turn.UserMessage.ReplaceLineEndings(" ");
        return new TurnItem
        {
            TraceId = turn.TraceId,
            Title = turn.Status == Telemetry.StatusCancelled ? $"{title}  ({Strings.Stopped})" : title,
            Caption = string.Join(" · ", parts),
            Tooltip = turn.ToolNames is { } tools ? $"{title}\n\n{tools.Replace(",", ", ")}" : title,
            FailedVisibility = turn.Failed && turn.Status != Telemetry.StatusCancelled ? Visibility.Visible : Visibility.Collapsed,
        };
    }
}

/// <summary>A step in the selected turn's timeline.</summary>
public sealed class SpanItem
{
    public required SpanRecord Span { get; init; }
    public required string Label { get; init; }
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public required Thickness Indent { get; init; }
    public required Thickness BarMargin { get; init; }
    public required double BarWidth { get; init; }
    public required Brush BarBrush { get; init; }
    public required Brush IconBrush { get; init; }

    public static SpanItem From(SpanRecord span, SpanRecord turn, int depth)
    {
        var failed = span.Failed && span.Status != Telemetry.StatusCancelled;
        var (glyph, label, brushKey) = span.Operation switch
        {
            Telemetry.OpTurn => ("", Strings.TraceTurn, "TextFillColorTertiaryBrush"),
            Telemetry.OpChat => ("", Strings.TraceModelCall(span.Model), "AccentFillColorDefaultBrush"),
            Telemetry.OpTool => ("", Strings.TraceToolCall(span.ToolSource is { } source && source != "built-in" ? $"{span.ToolName} ({source})" : span.ToolName), "SystemFillColorSuccessBrush"),
            Telemetry.OpApproval => ("", Strings.TraceApproval(Strings.TraceDecision(span.Attributes.GetValueOrDefault(Telemetry.TagDecision))), "SystemFillColorCautionBrush"),
            _ => ("", span.Name, "TextFillColorTertiaryBrush"),
        };

        var detail = ObservabilityWindow.FormatDuration(span.DurationMs);
        if (span.InputTokens is { } input)
            detail = $"{Strings.TraceTokens($"{ChatMarkup.FormatTokens(input)}→{ChatMarkup.FormatTokens(span.OutputTokens ?? 0)}")} · {detail}";

        var total = Math.Max(turn.DurationMs, 1);
        var offset = Math.Clamp((span.StartedAt - turn.StartedAt).TotalMilliseconds / total, 0, 1);
        var brush = (Brush)Application.Current.Resources[failed ? "SystemFillColorCriticalBrush" : brushKey];
        return new SpanItem
        {
            Span = span,
            Label = label,
            Detail = detail,
            Glyph = failed ? "" : glyph,
            Indent = new Thickness(depth * 20, 0, 0, 0),
            BarMargin = new Thickness(offset * 200, 0, 0, 0),
            BarWidth = Math.Max(3, Math.Min(span.DurationMs / total, 1 - offset) * 200),
            BarBrush = brush,
            IconBrush = brush,
        };
    }
}
