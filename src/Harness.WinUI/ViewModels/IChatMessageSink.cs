// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Agent;

namespace Harness.WinUI.ViewModels;

/// <summary>
/// UI-facing sink for chat message DOM operations. Implemented by <see cref="Harness.WinUI.MainWindow"/>
/// (the only place that owns the WebView2 control) so <see cref="ChatViewModel"/> stays free of
/// WebView2 / XAML concerns. Mirrors the JS functions in Harness.MarkdownRendering.ChatShell.
/// </summary>
public interface IChatMessageSink
{
    /// <summary>Appends a new message bubble. <paramref name="html"/> is already-rendered HTML.</summary>
    Task AppendMessageAsync(string id, string role, string html);

    /// <summary>
    /// Replaces an existing message's rendered content. <paramref name="isFinal"/> gates Mermaid
    /// rendering — only pass true once streaming has completed for that message.
    /// </summary>
    Task UpdateMessageContentAsync(string id, string html, bool isFinal);

    /// <summary>
    /// Shows the animated working line (with live elapsed time); null or empty hides it.
    /// <paramref name="paused"/> stops the elapsed time (e.g. while waiting for the user) until the next unpaused status.
    /// </summary>
    Task SetMessageStatusAsync(string id, string? status, bool paused = false);

    /// <summary>Adds or updates a step row (tool call / reasoning). <paramref name="html"/> must already be encoded.</summary>
    Task UpsertMessageStepAsync(string id, string stepId, string html, StepState state);

    /// <summary>Sets the muted footer under a reply; <paramref name="tooltip"/> holds the full breakdown.</summary>
    Task SetMessageMetaAsync(string id, string text, string? tooltip);

    /// <summary>
    /// Shows an artifact in the side panel: live content while it streams, then — once
    /// <see cref="Harness.Core.Artifacts.ArtifactSegment.IsComplete"/> — saved and rendered.
    /// </summary>
    Task PresentArtifactAsync(Harness.Core.Artifacts.ArtifactSegment artifact);

    /// <summary>
    /// Called when a tool call succeeds: if the tool comes with an MCP App view, shows it in the reply.
    /// <paramref name="result"/> is what the tool returned (for MCP tools, the CallToolResult as JSON).
    /// </summary>
    Task PresentToolAppAsync(string id, ToolCallStarted call, object? result);

    /// <summary>Removes every message from the transcript and closes the artifact panel.</summary>
    Task ClearConversationAsync();

    /// <summary>Makes a completed artifact from a reopened conversation available to its card, without showing it.</summary>
    Task RestoreArtifactAsync(Harness.Core.Artifacts.ArtifactSegment artifact);
}

public enum StepState
{
    Running,
    Waiting,
    Done,
    Error,
}
