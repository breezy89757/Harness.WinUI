// Harness.WinUI — Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Harness.MarkdownRendering;

/// <summary>
/// Markdown → HTML for chat message bubbles. Adapted from READU.md's Markdown parser but
/// simplified — no table of contents, no file-relative
/// <c>&lt;base&gt;</c> href, since chat messages aren't tied to a document on disk.
/// </summary>
public static class ChatMarkdownRenderer
{
    // DisableHtml: model output is untrusted (tool results can carry prompt injection), and raw HTML
    // like <img onerror=...> would run script inside the WebView — which can post approval messages.
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseAutoIdentifiers()
        .DisableHtml()
        .Build();

    /// <summary>Renders Markdown to a body-only HTML fragment, tagging Mermaid blocks with a content hash.</summary>
    public static string RenderBody(string markdownContent)
    {
        var html = Markdown.ToHtml(markdownContent ?? string.Empty, s_pipeline);
        return AddMermaidHashes(html);
    }

    private static string AddMermaidHashes(string html) =>
        Regex.Replace(html,
            @"(<(?:pre|code)\s+class=""(?:language-)?mermaid"")([^>]*>)([\s\S]*?)(</(?:pre|code)>)",
            m =>
            {
                var content = m.Groups[3].Value;
                var hash = ComputeShortHash(content);
                return $"{m.Groups[1].Value} data-mermaid-hash=\"{hash}\"{m.Groups[2].Value}{content}{m.Groups[4].Value}";
            },
            RegexOptions.IgnoreCase);

    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
