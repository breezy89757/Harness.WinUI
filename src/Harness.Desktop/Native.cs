// Harness.WinUI — Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Harness.Desktop;

/// <summary>The few Win32 calls the desktop agent needs: the foreground window and the emergency-stop key.</summary>
internal static class Native
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>Brings the window to the front. A child control (the desktop's icon list) is brought forward through its top-level window.</summary>
    public static void Focus(IntPtr hWnd)
    {
        var root = GetAncestor(hWnd, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero)
            hWnd = root;
        if (IsIconic(hWnd))
            ShowWindow(hWnd, 9); // SW_RESTORE
        SetForegroundWindow(hWnd);
    }

    /// <summary>True while the person holds Esc: whatever the agent is doing stops at once.</summary>
    public static bool StopRequested() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;
}
