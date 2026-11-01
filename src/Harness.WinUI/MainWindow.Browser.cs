// Harness.WinUI — Licensed under the MIT License.

using Harness.Browser;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>The browser agent (operate_browser): offered to the main agent only while the setting is on.</summary>
public sealed partial class MainWindow
{
    private void ApplyBrowserSetting() =>
        _tools.SetExtraTools("browser", AppPreferences.Load().BrowserControl ? context => [BrowserTool.Create(context)] : null);
}
