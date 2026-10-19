// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>
/// A short trail for problems that leave nothing else behind: a XAML crash only shows as 0xc000027b in the event log, and a
/// window that stops responding for a while leaves no trace at all. Lines go to diag.log in the data folder (kept small).
/// </summary>
internal static class Diagnostics
{
    private const long MaxBytes = 256 * 1024;
    private const double HungAfterSeconds = 6;

    private static readonly Lock s_lock = new();
    private static readonly Lock s_ioLock = new(); // the file; kept apart so the UI thread never waits behind a disk write
    private static string s_lastStep = "(none yet)";
    private static long s_lastStepAt = Stopwatch.GetTimestamp();
    private static long s_beat = Stopwatch.GetTimestamp();
    private static bool s_reported;
    private static long s_hungAt;
    private static Timer? s_watchdog;

    /// <summary>What the UI thread is about to do; if it stops responding, the log says it was last seen here.</summary>
    public static void Step(string what)
    {
        lock (s_lock)
        {
            s_lastStep = what;
            s_lastStepAt = Stopwatch.GetTimestamp();
        }
    }

    public static void Log(string line)
    {
        try
        {
            lock (s_ioLock)
            {
                var path = AppPaths.Combine("diag.log");
                Directory.CreateDirectory(AppPaths.DataRoot);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {line}\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never get in the way.
        }
    }

    /// <summary>Every 2 seconds asks the UI thread to answer; when it doesn't for a while, records where it was last seen.</summary>
    public static void StartUiWatchdog(DispatcherQueue ui)
    {
        s_watchdog = new Timer(_ =>
        {
            ui.TryEnqueue(() => Volatile.Write(ref s_beat, Stopwatch.GetTimestamp()));

            var silent = Stopwatch.GetElapsedTime(Volatile.Read(ref s_beat)).TotalSeconds;
            string step;
            double sinceStep;
            lock (s_lock)
            {
                step = s_lastStep;
                sinceStep = Stopwatch.GetElapsedTime(s_lastStepAt).TotalSeconds;
            }

            if (silent > HungAfterSeconds && !s_reported)
            {
                s_reported = true;
                s_hungAt = Stopwatch.GetTimestamp();
                Log($"UI thread has not answered for {silent:F0}s. Last step: {step} ({sinceStep:F0}s ago).");
            }
            else if (silent <= HungAfterSeconds && s_reported)
            {
                s_reported = false;
                Log($"UI thread answers again, after about {Stopwatch.GetElapsedTime(s_hungAt).TotalSeconds + HungAfterSeconds:F0}s.");
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }
}
