// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;

namespace Harness.WinUI;

/// <summary>
/// Work started from an event handler. An exception that leaves an <c>async void</c> handler has nowhere to go and ends the app, so
/// the work is run here and whatever goes wrong is logged instead.
/// </summary>
internal static class UiGuard
{
    public static async void Run(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Log($"{what} failed: {ex}");
        }
    }
}
