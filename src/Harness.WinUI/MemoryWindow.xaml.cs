// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Harness.Core.Config;
using Harness.Core.History;
using Harness.Core.Memory;

namespace Harness.WinUI;

/// <summary>
/// The memory window: the notes and the conversations they came from as a graph you can search, open and edit, and the
/// consolidation of the notes (the model suggests, the user picks). The page itself is <see cref="MemoryPage"/>.
/// </summary>
public sealed partial class MemoryWindow : Microsoft.UI.Xaml.Window
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly MemoryStore _store;
    private readonly ConversationStore? _history;
    private readonly Func<string?> _currentConversationId;
    private readonly Func<string?> _sandboxRoot;
    private readonly Func<(IChatClient? Client, string? Model)> _model;
    private readonly Action<string, long> _openConversation;
    private MemoryConsolidator? _consolidator;
    private ConsolidationPlan? _plan;
    private CancellationTokenSource? _asking;
    private bool _pageReady;
    private bool _pageLoaded;
    private IReadOnlyList<string> _pendingFocus = [];

    /// <param name="model">A chat client for consolidating (not recorded) and its model's name; the client is null when no model is set up.</param>
    /// <param name="openConversation">Opens a conversation in the main window at a message (the position, or 0 for the start).</param>
    public MemoryWindow(
        MemoryStore store, ConversationStore? history, Func<string?> currentConversationId, Func<string?> sandboxRoot,
        Func<(IChatClient? Client, string? Model)> model, Action<string, long> openConversation)
    {
        InitializeComponent();
        _store = store;
        _history = history;
        _currentConversationId = currentConversationId;
        _sandboxRoot = sandboxRoot;
        _model = model;
        _openConversation = openConversation;
        Title = $"{Strings.MemoryTitle} — Harness.WinUI";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Harness.ico"));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(820 * scale)));
        _ = StartAsync();
        Closed += (_, _) => _asking?.Cancel();
    }

    private async Task StartAsync()
    {
        await PageView.EnsureCoreWebView2Async();
        var core = PageView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
        core.WebMessageReceived += OnMessage;
        // Nothing here leads anywhere else: once the page has loaded it is the whole window.
        core.NavigationCompleted += (_, _) => _pageReady = true;
        core.NavigationStarting += (_, e) =>
        {
            if (_pageReady)
                e.Cancel = true;
        };
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        PageView.NavigateToString(MemoryPage.Html(nonce));
    }

    /// <summary>Points out these memories in the list (those a reply was given); when the page is not ready yet, as soon as it is.</summary>
    public void Focus(IReadOnlyList<string> ids)
    {
        if (_pageLoaded)
            Send(new { type = "focus", ids });
        else
            _pendingFocus = ids;
    }

    /// <summary>Shows the notes again (after the agent saved or forgot one).</summary>
    public void Refresh() => Send(new { type = "data", data = GraphJson(), remind = AppPreferences.Load().MemoryRemindAfter });

    private object GraphJson()
    {
        var graph = MemoryGraph.Build(_store, _history, AppPreferences.Load().MemoryArchiveDays, _currentConversationId());
        // Sent as a tree, not a string: the page reads it as an object.
        return JsonNode.Parse(MemoryGraph.ToJson(graph))!;
    }

    private void Send(object message)
    {
        if (PageView.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, s_json));
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = JsonNode.Parse(args.WebMessageAsJson)!.AsObject();
            string Str(string name) => message[name]?.GetValue<string>() ?? string.Empty;
            switch (Str("type"))
            {
                case "ready":
                    var preferences = AppPreferences.Load();
                    _pageLoaded = true;
                    Send(new { type = "init", strings = Strings.MemoryPage(), enabled = preferences.MemoryEnabled, remind = preferences.MemoryRemindAfter, data = GraphJson(), focus = _pendingFocus });
                    _pendingFocus = [];
                    break;
                case "search":
                    Search(Str("query"));
                    break;
                case "save":
                    Save(Str("id"), Str("text"), Str("memType"), Str("status"));
                    break;
                case "create":
                    Create(Str("text"), Str("memType"), Str("scope"));
                    break;
                case "forget":
                    if (_store.Delete(Str("id")))
                        Send(new { type = "toast", text = Strings.MemoryPage()["forgot"], undoId = Str("id") });
                    Refresh();
                    break;
                case "restore":
                    _store.Restore(Str("id"));
                    Refresh();
                    break;
                case "revive":
                    _store.Update(Str("id"), i => i with { LastUsed = DateTimeOffset.UtcNow });
                    Refresh();
                    break;
                case "openConversation":
                    _openConversation(Str("id"), message["seq"]?.GetValue<long>() ?? 0);
                    break;
                case "estimate":
                    Estimate();
                    break;
                case "consolidate":
                    _ = ConsolidateAsync();
                    break;
                case "cancelConsolidate":
                    _asking?.Cancel();
                    break;
                case "apply":
                    Apply(message["ops"]?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? []);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            // A message the page shouldn't have sent: ignore it.
        }
    }

    private void Search(string query)
    {
        var memories = _store.Search(query, null, 12, includeArchived: true)
            .Select(i => new { i.Id, i.Text }).ToList();
        var sessions = _history is null || string.IsNullOrWhiteSpace(query)
            ? []
            : _history.Search(query, limit: 12).Select(h => new { id = h.ConversationId, title = h.Title, at = h.At, seq = h.Seq, snippet = h.Snippet }).ToList();
        Send(new { type = "search", memories, sessions });
    }

    private void Save(string id, string text, string type, string status)
    {
        try
        {
            _store.Update(id, i => i with
            {
                Text = text,
                Type = Enum.TryParse<MemoryType>(type, ignoreCase: true, out var t) ? t : i.Type,
                Status = status == "new" ? MemoryStatus.New : MemoryStatus.Settled,
            });
            Send(new { type = "toast", text = Strings.MemoryPage()["saved"] });
        }
        catch (ArgumentException ex)
        {
            Send(new { type = "error", text = ex.Message });
        }
        Refresh();
    }

    private void Create(string text, string type, string scope)
    {
        try
        {
            var sandbox = _sandboxRoot();
            var itemScope = scope == "sandbox" && !string.IsNullOrWhiteSpace(sandbox) ? MemoryItem.SandboxScope(sandbox) : MemoryItem.GlobalScope;
            _store.Add(text, Enum.TryParse<MemoryType>(type, ignoreCase: true, out var t) ? t : MemoryType.Fact, itemScope, "user", status: MemoryStatus.Settled);
        }
        catch (ArgumentException ex)
        {
            Send(new { type = "error", text = ex.Message });
        }
        Refresh();
    }

    #region Consolidation

    private void Estimate()
    {
        if (_history is null)
        {
            Send(new { type = "error", text = Strings.MemoryPage()["noModel"] });
            return;
        }

        _consolidator = new MemoryConsolidator(_store, _history);
        var request = _consolidator.BuildRequest(out var conversations);
        var tokens = MemoryConsolidator.EstimateTokens(request);
        var (_, model) = _model();
        var cost = AppPreferences.Load() is { } p && p.PriceFor(model) is { } price
            ? $"{p.Currency} {(price.Input * tokens + price.Output * 600) / 1_000_000m:0.####}"
            : null;
        Send(new { type = "estimate", notes = _store.All().Count, conversations, tokens, cost });
    }

    private async Task ConsolidateAsync()
    {
        var (client, _) = _model();
        if (client is null || _history is null)
        {
            Send(new { type = "error", text = Strings.MemoryPage()["noModel"] });
            return;
        }

        _asking?.Cancel();
        _asking = new CancellationTokenSource();
        var token = _asking.Token;
        try
        {
            var consolidator = _consolidator = new MemoryConsolidator(_store, _history);
            var request = consolidator.BuildRequest(out var conversations);
            _plan = await consolidator.PlanAsync(client, request, conversations, token).ConfigureAwait(true);
            Send(new
            {
                type = "plan",
                dropped = _plan.Dropped,
                ops = _plan.Ops.Select(o => new { kind = o.Kind, ids = o.Ids, text = o.Text, reason = o.Reason }),
            });
        }
        catch (OperationCanceledException)
        {
            // The user cancelled.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Send(new { type = "error", text = Strings.MemoryPage()["failed"].Replace("{0}", ex.Message) });
        }
    }

    private void Apply(IReadOnlyList<int> chosen)
    {
        if (_plan is null || _consolidator is null)
            return;
        var ops = chosen.Where(i => i >= 0 && i < _plan.Ops.Count).Distinct().Select(i => _plan.Ops[i]).ToList();
        var count = _consolidator.Apply(ops, _sandboxRoot());
        _plan = null;
        Send(new { type = "applied", count });
        Refresh();
    }

    #endregion

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
