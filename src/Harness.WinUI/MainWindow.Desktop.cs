// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Config;
using Harness.Desktop;

namespace Harness.WinUI;

/// <summary>The desktop agent (operate_desktop): offered to the main agent only while the setting is on.</summary>
public sealed partial class MainWindow
{
    private void ApplyDesktopSetting() =>
        _tools.SetExtraTools("desktop", AppPreferences.Load().DesktopControl ? context => [DesktopTool.Create(context), DesktopTool.CreateListWindows(context)] : null);
}
