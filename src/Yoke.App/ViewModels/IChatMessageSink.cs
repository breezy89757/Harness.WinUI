// Yoke — Licensed under the MIT License.

namespace Yoke.App.ViewModels;

/// <summary>
/// UI-facing sink for chat message DOM operations. Implemented by <see cref="Yoke.App.MainWindow"/>
/// (the only place that owns the WebView2 control) so <see cref="ChatViewModel"/> stays free of
/// WebView2 / XAML concerns. Mirrors the JS functions in Yoke.MarkdownRendering.ChatShell.
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

    /// <summary>Shows the animated working line (with live elapsed time); null or empty hides it.</summary>
    Task SetMessageStatusAsync(string id, string? status);

    /// <summary>Adds or updates a step row (tool call / reasoning). <paramref name="html"/> must already be encoded.</summary>
    Task UpsertMessageStepAsync(string id, string stepId, string html, StepState state);

    /// <summary>Sets the muted footer under a reply; <paramref name="tooltip"/> holds the full breakdown.</summary>
    Task SetMessageMetaAsync(string id, string text, string? tooltip);

    /// <summary>
    /// Shows an artifact in the side panel: live content while it streams, then — once
    /// <see cref="Yoke.Core.Artifacts.ArtifactSegment.IsComplete"/> — saved and rendered.
    /// </summary>
    Task PresentArtifactAsync(Yoke.Core.Artifacts.ArtifactSegment artifact);

    /// <summary>Removes every message from the transcript and closes the artifact panel.</summary>
    Task ClearConversationAsync();

    /// <summary>Makes a completed artifact from a reopened conversation available to its card, without showing it.</summary>
    Task RestoreArtifactAsync(Yoke.Core.Artifacts.ArtifactSegment artifact);
}

public enum StepState
{
    Running,
    Waiting,
    Done,
    Error,
}
