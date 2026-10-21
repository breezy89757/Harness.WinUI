// Harness.WinUI — Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Harness.Core.Tools;

/// <param name="Status">pending, in_progress or completed.</param>
public sealed record TaskItem(string Content, string Status)
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>What the model sent for one task (update_tasks).</summary>
public sealed record TaskInput(
    [property: JsonPropertyName("content"), Description("What to do, in a few words (the user reads it).")] string Content,
    [property: JsonPropertyName("status"), Description("pending, in_progress or completed.")] string Status);

/// <summary>The agent's task list for the conversation: set by the model, shown to the user above the message box.</summary>
public sealed class TaskList
{
    private IReadOnlyList<TaskItem> _items = [];

    public IReadOnlyList<TaskItem> Items => Volatile.Read(ref _items);

    /// <summary>Raised (on whatever thread made the change) when the list changes.</summary>
    public event EventHandler? Changed;

    public void Set(IReadOnlyList<TaskItem> items)
    {
        Volatile.Write(ref _items, items);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (Items.Count > 0)
            Set([]);
    }
}

/// <summary>update_tasks: Code mode's way to plan several steps and show progress. It changes nothing but the list on screen.</summary>
public static class TaskTools
{
    public const string Name = "update_tasks";
    private const int MaxTasks = 30;
    private const int MaxLength = 200;

    public static AITool Create(TaskList list) => AIFunctionFactory.Create(
        ([Description("The whole list, every task each time (not just the changes), in the order they will be done.")] List<TaskInput> tasks) => Update(list, tasks),
        Name,
        "Keep a short task list for work with several steps, shown to the user. Send the whole list each time. Have one task in_progress at a time and mark it completed as soon as it is done. " +
        "Don't use it for a single small change.");

    private static string Update(TaskList list, List<TaskInput>? tasks)
    {
        if (tasks is null)
            return "Error: tasks is missing. Send the whole list.";

        var items = new List<TaskItem>();
        foreach (var task in tasks.Take(MaxTasks))
        {
            var content = (task.Content ?? string.Empty).ReplaceLineEndings(" ").Trim();
            if (content.Length == 0)
                continue;
            items.Add(new TaskItem(content.Length > MaxLength ? content[..MaxLength] : content, Normalize(task.Status)));
        }

        list.Set(items);

        var done = items.Count(i => i.Status == TaskItem.Completed);
        var current = items.FirstOrDefault(i => i.Status == TaskItem.InProgress) ?? items.FirstOrDefault(i => i.Status == TaskItem.Pending);
        var more = items.Count(i => i.Status == TaskItem.InProgress) > 1 ? " More than one task is in_progress; keep it to one." : "";
        return items.Count == 0
            ? "The task list is empty."
            : $"Task list updated: {done} of {items.Count} completed." + (current is null ? "" : $" Next: {current.Content}.") + more;
    }

    private static string Normalize(string? status) => (status ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
    {
        "completed" or "complete" or "done" or "finished" => TaskItem.Completed,
        "in_progress" or "inprogress" or "doing" or "active" or "started" => TaskItem.InProgress,
        _ => TaskItem.Pending,
    };
}
