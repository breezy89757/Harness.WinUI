// Harness.WinUI — Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Harness.Core.Config;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>
/// Making the app fit the person using it: how wide the conversation gets, the agent's change_appearance tool (the same
/// choices, by asking), and the tips on an empty conversation that tell the person this is possible.
/// </summary>
public sealed partial class MainWindow
{
    private void ConfigureAppearance() =>
        _tools.SetAppearance([.. _toolbarItems.Select(i => i.Id)], ChangeAppearanceAsync);

    /// <summary>Applies the saved conversation width to the chat page (after it loads, and on every change).</summary>
    private Task ApplyWidthAsync() =>
        ExecuteShellScriptAsync($"setConversationWidth({ToJs(Appearance.WidthLength(AppPreferences.Load().ConversationWidth))});");

    /// <summary>change_appearance: one checked change, made on the UI thread. Returns what happened, for the model.</summary>
    private async Task<string> ChangeAppearanceAsync(string setting, string value)
    {
        var result = string.Empty;
        await RunOnUIThreadAsync(async () => result = await ChangeAppearanceCoreAsync(setting, value));
        return result;
    }

    private async Task<string> ChangeAppearanceCoreAsync(string setting, string value)
    {
        switch (setting)
        {
            case Appearance.SettingTextSize:
                _zoom = int.Parse(value, CultureInfo.InvariantCulture);
                (AppPreferences.Load() with { TextZoom = _zoom }).Save();
                await ApplyZoomAsync();
                ShowHint($"{_zoom}%");
                return $"The text size is now {_zoom}%.";

            case Appearance.SettingConversationWidth:
                (AppPreferences.Load() with { ConversationWidth = value }).Save();
                await ApplyWidthAsync();
                return $"The conversation width is now {value}. (It only shows when the window is wider than that.)";

            default: // hide_toolbar_button / show_toolbar_button
                var hide = setting == Appearance.SettingHideToolbarButton;
                var preferences = AppPreferences.Load();
                HashSet<string> chosen = [.. preferences.HiddenToolbarButtons ?? []];
                _ = hide ? chosen.Add(value) : chosen.Remove(value);
                var ordered = _toolbarItems.Select(i => i.Id).Where(chosen.Contains).ToList();
                (preferences with { HiddenToolbarButtons = ordered.Count > 0 ? ordered : null }).Save();
                ApplyToolbar(HiddenToolbarButtons());
                return hide
                    ? $"The {value} button is hidden from the toolbar; it is still in the More menu."
                    : value == "memory" && !preferences.MemoryEnabled
                        ? "The memory button only shows while memory is turned on in Settings → Memory."
                        : $"The {value} button is shown on the toolbar.";
        }
    }

    #region Tips

    private sealed record Tip(string Id, string Text);

    /// <summary>The tips worth showing now: not closed by the user, and not about something already turned on.</summary>
    private static List<Tip> EligibleTips(AppPreferences preferences)
    {
        if (!preferences.ShowTips)
            return [];
        List<Tip> tips = [new("appearance", Strings.TipAppearance), new("toolbar", Strings.TipToolbar)];
        if (!preferences.MemoryEnabled)
            tips.Add(new("memory", Strings.TipMemory));
        if (!preferences.SearchHistory)
            tips.Add(new("history", Strings.TipHistory));
        return [.. tips.Where(t => preferences.DismissedTips?.Contains(t.Id) != true)];
    }

    /// <summary>Hands the chat page the tips to show on an empty conversation (after it loads, and when settings change).</summary>
    private Task RefreshTipsAsync()
    {
        var tips = JsonSerializer.Serialize(EligibleTips(AppPreferences.Load()).Select(t => new { id = t.Id, text = t.Text }));
        var labels = JsonSerializer.Serialize(new { tip = Strings.TipLabel, dismiss = Strings.TipDismiss, off = Strings.TipsTurnOff });
        return ExecuteShellScriptAsync($"setTips({tips}, {labels});");
    }

    private static void DismissTip(string id)
    {
        var preferences = AppPreferences.Load();
        List<string> dismissed = [.. preferences.DismissedTips ?? []];
        if (!dismissed.Contains(id))
            dismissed.Add(id);
        (preferences with { DismissedTips = dismissed }).Save();
    }

    private static void TurnTipsOff() => (AppPreferences.Load() with { ShowTips = false }).Save();

    #endregion
}
