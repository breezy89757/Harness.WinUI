// Harness.WinUI — Licensed under the MIT License.

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using Harness.Core.Tools;

namespace Harness.WinUI;

/// <summary>The agent's task list (Code mode's update_tasks) as a small panel above the message box.</summary>
public sealed partial class MainWindow
{
    private bool _tasksCollapsed;

    private void ConfigureTasks()
    {
        _tools.Tasks.Changed += (_, _) => DispatcherQueue.TryEnqueue(RenderTasks);
        // Saved with the conversation, and shown again when it is reopened.
        ViewModel.TasksProvider = () => _tools.Tasks.Items.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(_tools.Tasks.Items);
        ViewModel.TasksLoader = json =>
        {
            try
            {
                _tools.Tasks.Set(string.IsNullOrEmpty(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<TaskItem>>(json) ?? []);
            }
            catch (System.Text.Json.JsonException)
            {
                _tools.Tasks.Clear();
            }
        };
    }

    private void TaskHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        _tasksCollapsed = !_tasksCollapsed;
        RenderTasks();
    }

    private void RenderTasks()
    {
        var items = _tools.Tasks.Items;
        TaskPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskItemsPanel.Children.Clear();
        if (items.Count == 0)
            return;

        var done = items.Count(i => i.Status == TaskItem.Completed);
        TaskHeaderText.Text = Strings.TasksHeader(done, items.Count);
        TaskChevron.Glyph = _tasksCollapsed ? "" : "";
        TaskItemsPanel.Visibility = _tasksCollapsed ? Visibility.Collapsed : Visibility.Visible;

        foreach (var item in items)
        {
            var (mark, brush) = item.Status switch
            {
                TaskItem.Completed => ("✓", "TextFillColorSecondaryBrush"),
                TaskItem.InProgress => ("◐", "AccentTextFillColorPrimaryBrush"),
                _ => ("○", "TextFillColorSecondaryBrush"),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = mark,
                Width = 16,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush],
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Content,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 900,
                FontWeight = item.Status == TaskItem.InProgress ? FontWeights.SemiBold : FontWeights.Normal,
                TextDecorations = item.Status == TaskItem.Completed ? TextDecorations.Strikethrough : TextDecorations.None,
                Foreground = item.Status == TaskItem.Completed
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            });
            TaskItemsPanel.Children.Add(row);
        }
    }
}
