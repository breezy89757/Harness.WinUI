// Harness.WinUI — Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Harness.Core.Artifacts;

public enum ArtifactType
{
    Html,
    Svg,
    Mermaid,
    Markdown,
}

public abstract record ReplySegment;

public sealed record TextSegment(string Text) : ReplySegment;

/// <param name="IsComplete">False while the closing tag hasn't streamed in yet.</param>
public sealed record ArtifactSegment(string Id, ArtifactType Type, string Title, string Content, bool IsComplete) : ReplySegment;

/// <summary>
/// Splits a (possibly still streaming) reply into plain text and
/// <c>&lt;artifact id="…" type="…" title="…"&gt;…&lt;/artifact&gt;</c> blocks. Parsing the text stream
/// rather than using a tool call is what lets the artifact panel show content while it's being written.
/// </summary>
public static partial class ArtifactParser
{
    private const string OpenTagStart = "<artifact";
    private const string CloseTag = "</artifact>";

    public static IReadOnlyList<ReplySegment> Parse(string text, bool streamIsComplete)
    {
        var segments = new List<ReplySegment>();
        var position = 0;
        var index = 0;

        while (position < text.Length)
        {
            var open = OpenTag().Match(text, position);
            if (!open.Success)
            {
                AddText(segments, HideDanglingTagStart(text[position..], streamIsComplete));
                break;
            }

            AddText(segments, text[position..open.Index]);

            var attributes = ParseAttributes(open.Groups["attrs"].Value);
            var contentStart = open.Index + open.Length;
            var close = text.IndexOf(CloseTag, contentStart, StringComparison.OrdinalIgnoreCase);
            var isComplete = close >= 0 || streamIsComplete;
            var content = close >= 0 ? text[contentStart..close] : text[contentStart..];

            index++;
            var type = ParseType(attributes.GetValueOrDefault("type"));
            var title = attributes.GetValueOrDefault("title") is { Length: > 0 } t ? t : $"Artifact {index}";
            var id = NormalizeId(attributes.GetValueOrDefault("id"), title, index);
            segments.Add(new ArtifactSegment(id, type, title, StripFences(content, isComplete), isComplete));

            if (close < 0)
                break;
            position = close + CloseTag.Length;
        }

        return segments;
    }

    private static void AddText(List<ReplySegment> segments, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            segments.Add(new TextSegment(text));
    }

    /// <summary>While streaming, don't flash "&lt;artif" as text before the rest of the tag arrives.</summary>
    private static string HideDanglingTagStart(string text, bool streamIsComplete)
    {
        if (streamIsComplete)
            return text;

        var lt = text.LastIndexOf('<');
        if (lt < 0)
            return text;

        var tail = text[lt..];
        var isPrefixOfOpenTag = OpenTagStart.StartsWith(tail, StringComparison.OrdinalIgnoreCase);
        var isUnfinishedOpenTag = tail.StartsWith(OpenTagStart, StringComparison.OrdinalIgnoreCase) && !tail.Contains('>');
        return isPrefixOfOpenTag || isUnfinishedOpenTag ? text[..lt] : text;
    }

    private static Dictionary<string, string> ParseAttributes(string attributes) =>
        Attribute().Matches(attributes).ToDictionary(
            m => m.Groups["name"].Value.ToLowerInvariant(),
            m => System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value),
            StringComparer.OrdinalIgnoreCase);

    private static ArtifactType ParseType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "svg" or "image/svg+xml" => ArtifactType.Svg,
        "mermaid" => ArtifactType.Mermaid,
        "markdown" or "md" or "text/markdown" or "document" => ArtifactType.Markdown,
        _ => ArtifactType.Html,
    };

    /// <summary>Ids become file names, so keep them to [a-z0-9-].</summary>
    private static string NormalizeId(string? id, string title, int index)
    {
        var candidate = InvalidIdChars().Replace((id ?? string.Empty).Trim().ToLowerInvariant(), "-").Trim('-');
        if (candidate.Length == 0)
            candidate = InvalidIdChars().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (candidate.Length == 0)
            candidate = $"artifact-{index}";
        return candidate.Length <= 64 ? candidate : candidate[..64];
    }

    /// <summary>Models sometimes wrap the content in a Markdown code fence inside the tag; drop it.</summary>
    private static string StripFences(string content, bool isComplete)
    {
        var trimmed = content.Trim('\r', '\n');
        var fence = LeadingFence().Match(trimmed);
        if (!fence.Success)
            return trimmed;

        var body = trimmed[fence.Length..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0 && body[closing..].Trim() == "```")
            body = body[..closing];
        else if (!isComplete)
            body = body.TrimEnd('`'); // closing fence partly streamed in

        return body.Trim('\r', '\n');
    }

    [GeneratedRegex(@"<artifact(?<attrs>\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex OpenTag();

    [GeneratedRegex(@"(?<name>[A-Za-z_-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')")]
    private static partial Regex Attribute();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex InvalidIdChars();

    // Only a fence around the whole artifact (``` / ```html / ```markdown …) — not a Markdown document
    // that happens to start with its own ```python block.
    [GeneratedRegex(@"^```(?:html|svg|xml|mermaid|markdown|md)?[^\S\r\n]*\r?\n", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFence();
}
