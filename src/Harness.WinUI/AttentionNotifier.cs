// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Harness.WinUI;

/// <summary>
/// Gets the user's attention when Harness.WinUI isn't the foreground window: a Windows app notification
/// (which the user can turn off per app in Windows Settings › Notifications) and a flashing taskbar
/// button until the window is brought back. Clicking the notification brings the window to the front.
/// </summary>
public sealed class AttentionNotifier : IDisposable
{
    private readonly nint _hwnd;
    private readonly Action _activate;
    private readonly bool _notificationsAvailable;

    /// <param name="activate">Brings the window to the front; called on the UI thread's dispatcher by the caller.</param>
    /// <param name="packaged">True when running as an MSIX package (Microsoft Store).</param>
    public AttentionNotifier(nint hwnd, bool packaged, Action activate)
    {
        _hwnd = hwnd;
        _activate = activate;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => _activate();
            if (packaged)
            {
                // Name, icon and the toast activator come from Package.appxmanifest.
                AppNotificationManager.Default.Register();
            }
            else
            {
                // Unpackaged: tell Windows what to show as the sender.
                var icon = Path.Combine(AppContext.BaseDirectory, "Images", "Square44x44Logo.targetsize-256_altform-unplated.png");
                AppNotificationManager.Default.Register("Harness.WinUI", new Uri(icon));
            }
            _notificationsAvailable = AppNotificationManager.IsSupported();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            // Notifications unavailable (e.g. policy): the taskbar flash still works.
            Debug.WriteLine($"App notifications unavailable: {ex.Message}");
        }
    }

    /// <summary>True when the user is looking at another window.</summary>
    public bool IsInBackground => GetForegroundWindow() != _hwnd;

    /// <summary>Notifies only if the window is in the background. Safe to call from any thread.</summary>
    public void Notify(string title, string body)
    {
        if (!IsInBackground)
            return;

        Flash();
        if (!_notificationsAvailable)
            return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body.Length > 200 ? body[..200] + "…" : body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Couldn't show a notification: {ex.Message}");
        }
    }

    private void Flash()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG, // flash the taskbar button until the window comes to the foreground
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    public void Dispose()
    {
        if (!_notificationsAvailable)
            return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
        }
    }

    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
