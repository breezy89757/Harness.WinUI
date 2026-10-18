// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tools;

/// <summary>The look-and-feel choices that belong to the user, and the only ones the agent may change for them.</summary>
public static class Appearance
{
    /// <summary>From the narrowest to no limit at all.</summary>
    public static readonly IReadOnlyList<string> Widths = ["narrow", "standard", "wide", "full"];

    public const string DefaultWidth = "wide";

    /// <summary>Text sizes in percent (Ctrl + plus / minus steps through the same ones).</summary>
    public static readonly IReadOnlyList<int> TextSizes = [80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200];

    public const string SettingTextSize = "text_size";
    public const string SettingConversationWidth = "conversation_width";
    public const string SettingHideToolbarButton = "hide_toolbar_button";
    public const string SettingShowToolbarButton = "show_toolbar_button";

    /// <summary>The CSS length the chat page limits its column to, for a width in <see cref="Widths"/>.</summary>
    public static string WidthLength(string? width) => width switch
    {
        "narrow" => "64em",
        "standard" => "76em",
        "full" => "none",
        _ => "84em",
    };

    /// <summary>A width in <see cref="Widths"/>, or the default for anything else.</summary>
    public static string NormalizeWidth(string? width) =>
        Widths.FirstOrDefault(w => string.Equals(w, width?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultWidth;
}

/// <summary>
/// change_appearance: lets the agent do what the user asks about how the app looks (wider conversation, bigger text, hide a
/// toolbar button) instead of sending them to Settings. It changes nothing else, and asks every time.
/// </summary>
public static class AppearanceTool
{
    public const string ServerName = "appearance";

    /// <param name="toolbarButtons">The ids of the buttons on the composer toolbar.</param>
    /// <param name="apply">Makes a checked change (setting, value) and says what happened, in a sentence for the model.</param>
    public static AITool Create(
        IToolApprover approver, ToolPermissionStore permissions, IReadOnlyList<string> toolbarButtons, Func<string, string, Task<string>> apply)
    {
        var function = AIFunctionFactory.Create(
            async (
                [Description("text_size, conversation_width, hide_toolbar_button or show_toolbar_button.")] string setting,
                [Description("text_size: a percent from 80 to 200. conversation_width: narrow, standard, wide or full. The toolbar settings: a button id.")] string value) =>
            {
                var (ok, checkedValue) = Check(setting, value, toolbarButtons);
                return ok ? await apply(setting.Trim().ToLowerInvariant(), checkedValue).ConfigureAwait(false) : "Error: " + checkedValue;
            },
            "change_appearance",
            "Change how Harness.WinUI looks, when the user asks (\"make the conversation wider\", \"bigger text\", \"hide the usage button\"). " +
            "It takes effect at once and the user is asked to approve every change. Settings: " +
            "text_size (percent, 80 to 200; the default is 100), " +
            "conversation_width (narrow, standard, wide or full; wide is the default), " +
            $"hide_toolbar_button and show_toolbar_button (value: one of {string.Join(", ", toolbarButtons)}). " +
            "Nothing else can be changed this way; for anything else tell the user where it is in Settings.");

        // It changes only how the app looks, never files or commands: asked about every time, and not stopped by read-only mode.
        return new ApprovalGatedFunction(function, ServerName, function.Name, readOnly: true, approver, permissions, canAlwaysAllow: false, readsPrivateData: true);
    }

    /// <summary>The value to apply (a text size snapped to a step, a width or button id as it is spelled here), or why it can't be.</summary>
    private static (bool Ok, string Value) Check(string setting, string value, IReadOnlyList<string> toolbarButtons)
    {
        value = (value ?? string.Empty).Trim();
        switch ((setting ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Appearance.SettingTextSize:
                if (!int.TryParse(value.TrimEnd('%', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                    return (false, "text_size needs a number of percent, such as 120.");
                var snapped = Appearance.TextSizes.MinBy(s => Math.Abs(s - percent));
                return (true, snapped.ToString(CultureInfo.InvariantCulture));

            case Appearance.SettingConversationWidth:
                var width = Appearance.Widths.FirstOrDefault(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase));
                return width is null
                    ? (false, $"conversation_width must be one of {string.Join(", ", Appearance.Widths)}.")
                    : (true, width);

            case Appearance.SettingHideToolbarButton:
            case Appearance.SettingShowToolbarButton:
                var button = toolbarButtons.FirstOrDefault(b => string.Equals(b, value, StringComparison.OrdinalIgnoreCase));
                return button is null
                    ? (false, $"The toolbar button must be one of {string.Join(", ", toolbarButtons)}.")
                    : (true, button);

            default:
                return (false, $"Unknown setting. Use {Appearance.SettingTextSize}, {Appearance.SettingConversationWidth}, {Appearance.SettingHideToolbarButton} or {Appearance.SettingShowToolbarButton}.");
        }
    }
}
