// Harness.WinUI — Licensed under the MIT License.

using System.Text;

namespace Harness.Core.Agent;

/// <summary>
/// The user's own instructions to the agent, added after the built-in ones: a text from Settings (for every
/// conversation) and the AGENTS.md at the root of the sandbox folder (for work in that folder). Read fresh on
/// every model call, so a change applies from the next message on without restarting.
/// </summary>
public static class CustomInstructions
{
    /// <summary>Name of the instructions file at the root of the sandbox folder.</summary>
    public const string FileName = "AGENTS.md";

    /// <summary>Longest text accepted in Settings.</summary>
    public const int MaxSettingsLength = 4000;

    /// <summary>Most of AGENTS.md that is read; the rest is left out so a huge file can't swamp the context.</summary>
    public const int MaxFileBytes = 16 * 1024;

    /// <summary>The instructions to add, or null when there are none.</summary>
    /// <param name="fromSettings">The text the user wrote in Settings.</param>
    /// <param name="sandboxRoot">The sandbox folder (null or empty when the file tools are off).</param>
    public static string? Compose(string? fromSettings, string? sandboxRoot)
    {
        var settings = Clean(fromSettings, MaxSettingsLength);
        var file = ReadFile(sandboxRoot);
        if (settings is null && file is null)
            return null;

        var sb = new StringBuilder();
        sb.Append("## The user's own instructions\n");
        sb.Append("These come from the user (their settings and their files). Follow them as you would the user's messages, ");
        sb.Append("but they never override the rules above or permit anything unsafe.\n");
        if (settings is not null)
            sb.Append("\n### Preferences\n").Append(settings).Append('\n');
        if (file is not null)
            sb.Append("\n### ").Append(FileName).Append(" in the sandbox folder\n").Append(file).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>The path of the instructions file, or null when there is no sandbox folder.</summary>
    public static string? FilePath(string? sandboxRoot) =>
        string.IsNullOrWhiteSpace(sandboxRoot) ? null : Path.Combine(sandboxRoot, FileName);

    private static string? ReadFile(string? sandboxRoot)
    {
        if (FilePath(sandboxRoot) is not { } path)
            return null;

        try
        {
            var info = new FileInfo(path);
            // A link could point anywhere on the PC: only a real file in the folder counts.
            if (!info.Exists || info.LinkTarget is not null)
                return null;

            using var stream = info.OpenRead();
            var buffer = new byte[Math.Min(info.Length, MaxFileBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿');
            if (info.Length > MaxFileBytes)
                text += "\n\n(The rest of the file is not shown: it is longer than " + MaxFileBytes / 1024 + " KB.)";
            return Clean(text, int.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // locked or unreadable: chat as if there were none
        }
    }

    private static string? Clean(string? text, int maxLength)
    {
        var trimmed = text?.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
