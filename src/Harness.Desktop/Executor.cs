// Harness.WinUI — Licensed under the MIT License.

using System.Drawing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Harness.Desktop;

/// <summary>
/// Does what the model asked, in one window. A real control is driven through its UI Automation pattern when it has one (no
/// coordinates involved); otherwise the mouse clicks its centre, and only ever inside the target window and only where the target
/// is really what is under the pointer (another window may be on top of it).
/// </summary>
/// <param name="pointIsTarget">Whether the target window (not another one lying over it) is what is at a screen point.</param>
/// <param name="shell">The target is the desktop or the taskbar, where typing could rename or delete things: only keys that move and open are allowed, and nothing is typed.</param>
internal sealed class Executor(IntPtr windowHandle, Rectangle region, Func<Point, bool>? pointIsTarget = null, bool shell = false)
{
    // Keys the model may not press: closing windows or reaching the system is not what a task needs.
    private static readonly HashSet<string> Blocked = ["alt+f4", "win", "ctrl+alt+delete", "ctrl+w", "ctrl+shift+esc"];

    // On the desktop or the taskbar: moving about and opening only (Delete would delete, F2 and typing would rename).
    private static readonly HashSet<string> ShellKeys = ["enter", "up", "down", "left", "right", "tab", "esc", "escape", "home", "end"];

    public string Click(UiElement e)
    {
        if (!e.Enabled) throw new InvalidOperationException("the element is disabled");
        Native.Focus(windowHandle);
        // A desktop icon: Invoke does nothing there, and a click would land on whatever window covers it. Select it, bring the
        // desktop forward and press Enter, which opens it the way the keyboard does.
        if (shell && e.Kind == "listitem" && e.Element is { } icon && icon.Patterns.SelectionItem.IsSupported)
        {
            icon.Patterns.SelectionItem.Pattern.Select();
            Native.Focus(windowHandle);
            Thread.Sleep(350);
            Keyboard.Type(VirtualKeyShort.ENTER);
            return "select + Enter";
        }
        if (e.Source == "uia" && e.Element is { } el)
        {
            try
            {
                if (el.Patterns.Invoke.IsSupported) { el.Patterns.Invoke.Pattern.Invoke(); return "invoke"; }
                if (el.Patterns.Toggle.IsSupported) { el.Patterns.Toggle.Pattern.Toggle(); return "toggle"; }
                if (el.Patterns.SelectionItem.IsSupported) { el.Patterns.SelectionItem.Pattern.Select(); return "select"; }
                if (el.Patterns.ExpandCollapse.IsSupported) { el.Patterns.ExpandCollapse.Pattern.Expand(); return "expand"; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the pattern refused (the control is busy or custom): fall through to a real click
            }
        }
        CheckPoint(e.Center);
        Mouse.Click(e.Center);
        return "mouse click";
    }

    public string DoubleClick(UiElement e)
    {
        if (!e.Enabled) throw new InvalidOperationException("the element is disabled");
        Native.Focus(windowHandle);
        CheckPoint(e.Center);
        Mouse.DoubleClick(e.Center);
        return "mouse double click";
    }

    public string Fill(UiElement e, string text)
    {
        if (shell) throw new InvalidOperationException("nothing can be typed on the desktop or the taskbar");
        Native.Focus(windowHandle);
        if (e.Source == "uia" && e.Element?.Patterns.Value.PatternOrDefault is { } value && !value.IsReadOnly.ValueOrDefault)
        {
            try
            {
                value.SetValue(text);
                return "value pattern";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // fall through to typing
            }
        }
        CheckPoint(e.Center);
        Mouse.Click(e.Center);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        return "typed";
    }

    public string TypeText(UiElement? e, string text)
    {
        if (shell) throw new InvalidOperationException("nothing can be typed on the desktop or the taskbar");
        Native.Focus(windowHandle);
        if (e != null)
        {
            CheckPoint(e.Center);
            Mouse.Click(e.Center);
        }
        Keyboard.Type(text);
        return "typed";
    }

    public string Keys(string combo)
    {
        var normalized = combo.Trim().ToLowerInvariant().Replace(" ", "");
        if (Blocked.Contains(normalized)) throw new InvalidOperationException($"the key combination {combo} is not allowed");
        if (shell && !ShellKeys.Contains(normalized)) throw new InvalidOperationException($"on the desktop or the taskbar only these keys are allowed: {string.Join(", ", ShellKeys.Order())}");
        var keys = normalized.Split('+').Select(Key).ToArray();
        Native.Focus(windowHandle);
        if (keys.Length == 1) Keyboard.Type(keys[0]); else Keyboard.TypeSimultaneously(keys);
        return "keys";
    }

    /// <summary>Shows the desktop the way Win+D does (minimises every window); only when the target is the desktop. <see cref="RestoreWindows"/> brings them back.</summary>
    public string ShowDesktop()
    {
        if (!shell) throw new InvalidOperationException("show_desktop is only for a task on the desktop itself");
        ShellApplication().MinimizeAll();
        Thread.Sleep(700);
        return "desktop shown";
    }

    /// <summary>Puts back the windows that <see cref="ShowDesktop"/> minimised (and only those).</summary>
    public static void RestoreWindows()
    {
        try
        {
            ShellApplication().UndoMinimizeALL();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // nothing sensible to do: the person can press Win+D again
        }
    }

    private static dynamic ShellApplication() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("the Windows shell is not available"))!;

    public string Scroll(UiElement e, string direction)
    {
        Native.Focus(windowHandle);
        CheckPoint(e.Center);
        Mouse.MoveTo(e.Center);
        Mouse.Scroll(direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? 3 : -3);
        return "scroll";
    }

    private void CheckPoint(Point screenPoint)
    {
        // Never outside the target window, whatever the model asks for.
        if (!region.Contains(screenPoint)) throw new InvalidOperationException("that point is outside the target window");
        // And never onto another window that lies over it: the click would land there.
        if (pointIsTarget is not null && !pointIsTarget(screenPoint))
            throw new InvalidOperationException("another window is covering that spot, so a click would land on it instead. Use a control's own action (click on a listed control) or ask the person to uncover the window");
    }

    internal static VirtualKeyShort Key(string name) => name switch
    {
        "ctrl" or "control" => VirtualKeyShort.CONTROL,
        "shift" => VirtualKeyShort.SHIFT,
        "alt" => VirtualKeyShort.ALT,
        "enter" or "return" => VirtualKeyShort.ENTER,
        "esc" or "escape" => VirtualKeyShort.ESCAPE,
        "tab" => VirtualKeyShort.TAB,
        "space" => VirtualKeyShort.SPACE,
        "backspace" => VirtualKeyShort.BACK,
        "delete" or "del" => VirtualKeyShort.DELETE,
        "home" => VirtualKeyShort.HOME,
        "end" => VirtualKeyShort.END,
        "up" => VirtualKeyShort.UP,
        "down" => VirtualKeyShort.DOWN,
        "left" => VirtualKeyShort.LEFT,
        "right" => VirtualKeyShort.RIGHT,
        "win" or "windows" => throw new InvalidOperationException("the Windows key is not allowed"),
        _ when name.Length == 1 && char.IsAsciiLetter(name[0]) => Enum.Parse<VirtualKeyShort>($"KEY_{char.ToUpperInvariant(name[0])}"),
        _ when name.Length == 1 && char.IsAsciiDigit(name[0]) => Enum.Parse<VirtualKeyShort>($"KEY_{name[0]}"),
        _ when name.StartsWith('f') && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12 => Enum.Parse<VirtualKeyShort>($"F{n}"),
        _ => throw new InvalidOperationException($"unknown key {name}"),
    };
}
