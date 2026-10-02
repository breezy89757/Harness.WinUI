// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace Harness.WinUI;

/// <summary>
/// Gets the user's attention when Harness.WinUI isn't the foreground window: a Windows app notification
/// (which the user can turn off per app in Windows Settings › Notifications) and a flashing taskbar
/// button until the window is brought back. Clicking the notification brings the window to the front.
/// The Store package uses the Windows App SDK's app notifications (its manifest declares the activator);
/// the portable build can't — they need the Windows App Runtime's shared packages, which a self-contained
/// app doesn't install — so it registers an AppUserModelID for the current user and shows Windows toasts.
/// </summary>
public sealed class AttentionNotifier : IDisposable
{
    private readonly nint _hwnd;
    private readonly Action _activate;
    private readonly bool _notificationsAvailable;
    private readonly ToastNotifier? _toastNotifier;
    private const string PortableAppId = "Harness.WinUI";

    /// <param name="activate">Brings the window to the front; called on the UI thread's dispatcher by the caller.</param>
    /// <param name="packaged">True when running as an MSIX package (Microsoft Store).</param>
    public AttentionNotifier(nint hwnd, bool packaged, Action activate)
    {
        _hwnd = hwnd;
        _activate = activate;
        try
        {
            if (packaged)
            {
                // Name, icon and the toast activator come from Package.appxmanifest.
                AppNotificationManager.Default.NotificationInvoked += (_, _) => _activate();
                AppNotificationManager.Default.Register();
                _notificationsAvailable = AppNotificationManager.IsSupported();
            }
            else
            {
                // Portable: what Windows shows as the sender (name and icon) for this AppUserModelID.
                using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{PortableAppId}"))
                {
                    key.SetValue("DisplayName", "Harness.WinUI");
                    key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Images", "Square44x44Logo.targetsize-256_altform-unplated.png"));
                }
                _toastNotifier = ToastNotificationManager.CreateToastNotifier(PortableAppId);
                _notificationsAvailable = true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or IOException)
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
        body = body.Length > 200 ? body[..200] + "…" : body;
        try
        {
            if (_toastNotifier is not null)
            {
                var content = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = content.GetElementsByTagName("text");
                texts[0].InnerText = title;
                texts[1].InnerText = body;
                var toast = new ToastNotification(content);
                // Clicks while Harness.WinUI is running arrive here; afterwards the toast just closes.
                toast.Activated += (_, _) => _activate();
                _toastNotifier.Show(toast);
                return;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
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
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, // flash the title bar and taskbar button until the window comes to the foreground
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    public void Dispose()
    {
        if (!_notificationsAvailable || _toastNotifier is not null)
            return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
        }
    }

    private const uint FLASHW_ALL = 0x3;
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
