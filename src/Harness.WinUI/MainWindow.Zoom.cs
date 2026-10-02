// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Harness.Core.Config;

namespace Harness.WinUI;

/// <summary>
/// Text size for the conversation and the composer, like a browser's zoom: Ctrl + plus / minus / 0
/// (keyboard accelerators while focus is in XAML; the chat page forwards the same keys and Ctrl + wheel
/// while focus is in it). Remembered across launches; a small indicator shows the new size.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly int[] s_zoomLevels = [80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200];
    private const double BaseFontSize = 14;
    private int _zoom = 100;
    private Storyboard? _zoomFade;

    private void ConfigureZoom()
    {
        var saved = AppPreferences.Load().TextZoom;
        _zoom = s_zoomLevels.Contains(saved) ? saved : 100;
        InputTextBox.FontSize = BaseFontSize * _zoom / 100;

        void Add(VirtualKey key, int step)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) => { e.Handled = true; StepZoom(step); };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        Add((VirtualKey)187, +1); // = / + on the main keyboard
        Add(VirtualKey.Add, +1);
        Add((VirtualKey)189, -1); // - on the main keyboard
        Add(VirtualKey.Subtract, -1);
        Add(VirtualKey.Number0, 0);
        Add(VirtualKey.NumberPad0, 0);
        // These are window-wide shortcuts; don't list them in every control's tooltip.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>+1 / -1 moves one level; 0 resets to 100%.</summary>
    private void StepZoom(int step)
    {
        var index = Array.IndexOf(s_zoomLevels, _zoom);
        var next = step == 0 ? 100 : s_zoomLevels[Math.Clamp(index + step, 0, s_zoomLevels.Length - 1)];
        if (next != _zoom)
        {
            _zoom = next;
            _ = ApplyZoomAsync();
            (AppPreferences.Load() with { TextZoom = _zoom }).Save();
        }
        ShowHint($"{_zoom}%");
    }

    /// <summary>Applies the current size to the chat page (after it loads, and on every change) and the composer.</summary>
    private Task ApplyZoomAsync()
    {
        InputTextBox.FontSize = BaseFontSize * _zoom / 100;
        return ExecuteShellScriptAsync($"setTextZoom({_zoom});");
    }

    /// <summary>A short-lived message over the conversation (text size, why a file couldn't be attached).</summary>
    private void ShowHint(string text, int milliseconds = 1100)
    {
        ZoomIndicatorText.Text = text;
        _zoomFade?.Stop();
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(120), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(milliseconds), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(milliseconds + 400), Value = 0 });
        Storyboard.SetTarget(fade, ZoomIndicator);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        _zoomFade = new Storyboard { Children = { fade } };
        _zoomFade.Begin();
    }
}
