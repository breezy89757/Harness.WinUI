// Harness.WinUI — Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using Harness.Core.Agent;
using Harness.Core.Artifacts;
using Harness.Core.Files;
using Harness.Core.Tools;
using Harness.MarkdownRendering;

namespace Harness.WinUI.ViewModels;

/// <summary>
/// HTML fragments and status text for the chat transcript: tool steps, approval cards, artifact cards,
/// reply footers. Everything that reaches the page is HTML-encoded here; the ViewModel only decides
/// what to show and when.
/// </summary>
internal static class ChatMarkup
{
    public static string ToolStepId(string callId) => "tool-" + callId;

    /// <summary>A reply as HTML: text segments as Markdown, artifacts as clickable cards.</summary>
    public static string ReplyHtml(IEnumerable<ReplySegment> segments)
    {
        var html = new StringBuilder();
        foreach (var segment in segments)
            html.Append(segment is ArtifactSegment artifact ? ArtifactCardHtml(artifact) : ChatMarkdownRenderer.RenderBody(((TextSegment)segment).Text));
        return html.ToString();
    }

    /// <summary>
    /// A tool result as JSON for the history database — the original structure when small (so replay
    /// renders it the same way, e.g. generated images), otherwise a truncated text summary.
    /// </summary>
    public static string? ResultJson(object? result)
    {
        if (result is null)
            return null;
        try
        {
            var json = (result as JsonElement? ?? JsonSerializer.SerializeToElement(result)).GetRawText();
            return json.Length <= 16_000 ? json : JsonSerializer.Serialize(Truncate(DescribeResult(result), 4000));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return JsonSerializer.Serialize(Truncate(result.ToString() ?? string.Empty, 4000));
        }
    }

    /// <summary>Clickable card standing in for an artifact in the chat; the content lives in the side panel.</summary>
    public static string ArtifactCardHtml(ArtifactSegment artifact)
    {
        var (badge, label) = artifact.Type switch
        {
            ArtifactType.Svg => ("SVG", "SVG"),
            ArtifactType.Mermaid => ("MMD", Strings.ArtifactDiagram),
            ArtifactType.Markdown => ("MD", Strings.ArtifactDocument),
            ArtifactType.Slides => ("PPT", Strings.ArtifactSlides),
            _ => ("</>", "HTML"),
        };
        var status = artifact.IsComplete ? Strings.ArtifactClickToOpen : Strings.ArtifactWriting;
        return $"""
            <div class='artifact-card{(artifact.IsComplete ? "" : " writing")}' data-artifact='{WebUtility.HtmlEncode(artifact.Id)}' role='button' tabindex='0'>
              <div class='artifact-badge'>{WebUtility.HtmlEncode(badge)}</div>
              <div class='artifact-meta'>
                <div class='artifact-title'>{WebUtility.HtmlEncode(artifact.Title)}</div>
                <div class='artifact-sub'>{WebUtility.HtmlEncode(label)} · {WebUtility.HtmlEncode(status)}</div>
              </div>
            </div>
            """;
    }

    /// <summary>Measured with gpt-image-2 at 1024px: low ≈19s, medium ≈51s, high ≈100s+.</summary>
    /// <param name="defaultQuality">The user's setting, used when the model didn't pass a quality.</param>
    public static string ImageStatus(ToolCallStarted call, string defaultQuality)
    {
        var requested = call.Arguments?.TryGetValue("quality", out var q) == true ? q?.ToString() : null;
        var quality = ImageGenerationTool.NormalizeQuality(string.IsNullOrWhiteSpace(requested) ? defaultQuality : requested);
        var estimate = quality switch { "high" => Strings.AboutTwoMinutes, "medium" => Strings.AboutSeconds(50), _ => Strings.AboutSeconds(20) };
        return Strings.GeneratingImage(quality, estimate);
    }

    public static string ReasoningStepHtml(string reasoning) =>
        $"<details><summary>{WebUtility.HtmlEncode(Strings.Reasoning)}</summary><div class='reasoning-text'>{WebUtility.HtmlEncode(reasoning)}</div></details>";

    // Relaxed escaping keeps 中文 readable; everything is HTML-encoded before it reaches the page.
    private static readonly JsonSerializerOptions s_compactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions s_prettyJson = new(s_compactJson) { WriteIndented = true };

    public static string ToolStepHtml(ToolCallStarted call, string? note = null, object? result = null, string? extraHtml = null)
    {
        var args = call.Arguments is { Count: > 0 } ? JsonSerializer.Serialize(Redact(call.Arguments), s_compactJson) : string.Empty;
        if (args.Length > 120)
            args = args[..117] + "...";

        var html = new StringBuilder($"<div><code>{WebUtility.HtmlEncode(call.Name)}</code>");
        if (args.Length > 0)
            html.Append($" <span>{WebUtility.HtmlEncode(args)}</span>");
        if (note is not null)
            html.Append($" — {WebUtility.HtmlEncode(note)}");
        else if (call.Name == "read_file" && result is not null && ReadCoverage(DescribeResult(result)) is { } coverage)
            html.Append($" — {WebUtility.HtmlEncode(coverage)}");

        if (result is not null && call.Name == ImageGenerationTool.Name && GeneratedImageUrl(result) is { } imageUrl)
            html.Append($"<a href='{imageUrl}'><img class='tool-image' src='{imageUrl}' alt='{WebUtility.HtmlEncode(Strings.GeneratedImage)}'></a>");
        else if (result is not null && DescribeResult(result) is { Length: > 0 } text)
        {
            if (CreatedFileUri(call.Name, text) is { } fileUri)
                html.Append($" <a class='tool-open' href='{WebUtility.HtmlEncode(fileUri)}'>{WebUtility.HtmlEncode(Strings.OpenFile)}</a>");
            html.Append($"<details><summary>{WebUtility.HtmlEncode(Strings.Result)}</summary><pre class='tool-result'>{WebUtility.HtmlEncode(Truncate(text, 4000))}</pre></details>");
        }

        return html.Append(extraHtml).Append("</div>").ToString();
    }

    private const int HelperLinesShown = 8;

    /// <summary>The steps a helper has taken, newest last, with the older ones counted.</summary>
    public static string HelperLogHtml(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return string.Empty;

        var html = new StringBuilder("<div class='helper-log'>");
        if (lines.Count > HelperLinesShown)
            html.Append($"<div>{WebUtility.HtmlEncode(Strings.HelperEarlier(lines.Count - HelperLinesShown))}</div>");
        foreach (var line in lines.Skip(lines.Count - HelperLinesShown))
            html.Append($"<div>{WebUtility.HtmlEncode(line)}</div>");
        return html.Append("</div>").ToString();
    }

    /// <summary>A helper agent's step while it works (or right after it finished): its question and what it is doing.</summary>
    public static string HelperStepHtml(string task, IReadOnlyList<string> lines, int steps, TimeSpan elapsed, bool finished, string stopped)
    {
        var state = finished ? Strings.HelperSummary(steps, elapsed) + (stopped.Length > 0 ? " " + Strings.HelperWhy(stopped) : "") : Strings.HelperRunning(steps, elapsed);
        return $"<div><code>{WebUtility.HtmlEncode(HelperAgentTool.Name)}</code> — {WebUtility.HtmlEncode(state)}" +
               $"<details><summary>{WebUtility.HtmlEncode(Strings.HelperQuestion)}</summary><pre class='tool-result'>{WebUtility.HtmlEncode(Truncate(task, 2000))}</pre></details>" +
               HelperLogHtml(lines) + "</div>";
    }

    /// <summary>
    /// What an edit_file or write_file is about to change, as a coloured diff (removed lines red, added lines green), so the
    /// person approving sees the change and not a JSON string. Null for any other tool.
    /// </summary>
    private static string? FileChangeDiffHtml(ToolApprovalRequest request, string? sandboxRoot)
    {
        if (request.ServerName != Harness.Core.Files.FileTools.ServerName ||
            !request.Arguments.TryGetValue("path", out var pathValue) || pathValue?.ToString() is not { Length: > 0 } path)
        {
            return null;
        }

        string? diff = null;
        if (request.ToolName == "edit_file" &&
            request.Arguments.TryGetValue("old_text", out var oldValue) && request.Arguments.TryGetValue("new_text", out var newValue))
        {
            diff = Harness.Core.Files.TextDiff.Unified(oldValue?.ToString() ?? string.Empty, newValue?.ToString() ?? string.Empty, context: 3, maxOutputLines: 80);
        }
        else if (request.ToolName == "write_file" && request.Arguments.TryGetValue("content", out var contentValue))
        {
            var content = contentValue?.ToString() ?? string.Empty;
            var existing = ExistingText(sandboxRoot, path);
            if (existing is null)
            {
                var lines = content.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
                diff = Strings.NewFile(lines.Length) + "\n" + string.Join('\n', lines.Take(24).Select(l => "+ " + l));
            }
            else
            {
                diff = Harness.Core.Files.TextDiff.Unified(existing, content, context: 3, maxOutputLines: 80);
            }
        }
        if (diff is null)
            return null;

        var html = new StringBuilder($"<b>{WebUtility.HtmlEncode(path)}</b>\n");
        foreach (var line in Truncate(diff, 6000).Split('\n'))
        {
            var css = line.StartsWith("+ ", StringComparison.Ordinal) ? "diff-add" : line.StartsWith("- ", StringComparison.Ordinal) ? "diff-del" : null;
            html.Append(css is null ? WebUtility.HtmlEncode(line) : $"<span class='{css}'>{WebUtility.HtmlEncode(line)}</span>").Append('\n');
        }
        return html.ToString().TrimEnd();
    }

    /// <summary>The text of a file inside the sandbox, or null if there is no such text file (or it is outside the sandbox).</summary>
    private static string? ExistingText(string? sandboxRoot, string path)
    {
        try
        {
            if (sandboxRoot is null)
                return null;
            var full = Path.GetFullPath(Path.Combine(sandboxRoot, path));
            var root = Path.GetFullPath(sandboxRoot).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) && new FileInfo(full).Length < 2_000_000
                ? File.ReadAllText(full)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex s_readLines =
        new(@"^[^\n]*— lines (\d+)-(\d+) of (\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex s_readWhole =
        new(@"^[^\n]*— (\d+) lines \(the whole file\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// How much of a file a read_file call covered, as the step shows it ("lines 1-240 of 993"), so it is plain when the
    /// agent only looked at the start of a file.
    /// </summary>
    private static string? ReadCoverage(string? result)
    {
        if (result is null)
            return null;
        if (s_readLines.Match(result) is { Success: true } part)
            return Strings.ReadLines(part.Groups[1].Value, part.Groups[2].Value, part.Groups[3].Value);
        if (s_readWhole.Match(result) is { Success: true } whole)
            return Strings.ReadWholeFile(whole.Groups[1].Value);
        return null;
    }

    /// <summary>Full view of what's about to run, so the user can judge it (secrets masked).</summary>
    public static string ApprovalStepHtml(ToolApprovalRequest request, string? sandboxRoot = null)
    {
        // A command reads best as itself, not as an escaped JSON string.
        var args = request.ToolName == CommandTool.Name && request.Arguments.TryGetValue("command", out var command) && command?.ToString() is { } text
            ? text + (request.Arguments.TryGetValue("timeout_seconds", out var timeout) && timeout is not null ? $"\n\n({Strings.CommandTimeout(timeout.ToString())})" : string.Empty)
            : request.Arguments.Count > 0 ? JsonSerializer.Serialize(Redact(request.Arguments), s_prettyJson) : "{}";
        var argsHtml = FileChangeDiffHtml(request, sandboxRoot) ?? WebUtility.HtmlEncode(Truncate(args, 4000));
        var where = request.ToolName == CommandTool.Name ? $"<div class='tool-where'>{WebUtility.HtmlEncode(Strings.CommandWhere)}</div>" : string.Empty;
        var alwaysLabel = request.AlwaysAllowScope is { } scope ? Strings.AlwaysAllowStartingWith(scope) : Strings.AlwaysAllow;
        var alwaysButton = request.CanAlwaysAllow ? $"<button data-decision='always'>{WebUtility.HtmlEncode(alwaysLabel)}</button>" : string.Empty;
        var sessionLabel = request.AlwaysAllowScope is { } sessionScope ? Strings.AllowForConversationStartingWith(sessionScope) : Strings.AllowForConversation;
        var sessionButton = request.CanAlwaysAllow ? $"<button data-decision='session'>{WebUtility.HtmlEncode(sessionLabel)}</button>" : string.Empty;
        return $"""
            <div><code>{WebUtility.HtmlEncode(request.ToolName)}</code><span class='tool-server'>({WebUtility.HtmlEncode(request.ServerName)})</span>
            {where}<pre class='tool-args'>{argsHtml}</pre>
            <div class='approval' data-call='{WebUtility.HtmlEncode(request.CallId)}'>
              <span>{WebUtility.HtmlEncode(Strings.AllowThisAction)}</span>
              <button class='primary' data-decision='allow'>{WebUtility.HtmlEncode(Strings.AllowOnce)}</button>
              {sessionButton}
              {alwaysButton}
              <button data-decision='deny'>{WebUtility.HtmlEncode(Strings.Deny)}</button>
            </div></div>
            """;
    }

    private static readonly System.Text.RegularExpressions.Regex s_sensitiveKey =
        new("token|secret|password|passwd|api[_-]?key|authorization|auth_header_value|credential", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Masks argument values whose names suggest credentials before they're shown on screen.</summary>
    private static Dictionary<string, object?> Redact(IEnumerable<KeyValuePair<string, object?>> arguments) =>
        arguments.ToDictionary(a => a.Key, a => s_sensitiveKey.IsMatch(a.Key) && a.Value is not null ? (object?)"••••••" : a.Value);

    /// <summary>The generate_image tool returns {"file": ...}; only files in its own output folder are shown.</summary>
    private static string? GeneratedImageUrl(object result)
    {
        try
        {
            var json = result as JsonElement? ?? JsonSerializer.SerializeToElement(result);
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("file", out var fileProperty) || fileProperty.GetString() is not { } file)
                return null;

            var full = Path.GetFullPath(file);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(ImageGenerationTool.OutputDirectory), StringComparison.OrdinalIgnoreCase))
                return null;

            return $"https://{ChatShell.ImagesHostName}/{Uri.EscapeDataString(Path.GetFileName(full))}";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The file a built-in create_* tool just wrote, as a file:// link (opened through <see cref="ShellLauncher"/>).</summary>
    private static string? CreatedFileUri(string toolName, string resultText)
    {
        if (toolName is not ("create_word_document" or "create_excel_workbook"))
            return null;

        var line = resultText.Split('\n').FirstOrDefault(l => l.StartsWith(FileTools.CreatedFilePrefix, StringComparison.Ordinal));
        var path = line?[FileTools.CreatedFilePrefix.Length..].Trim();
        return path is not null && Path.IsPathFullyQualified(path) ? new Uri(path).AbsoluteUri : null;
    }

    /// <summary>MCP results are {"content":[{"type":"text","text":...}, ...]}; show the text parts, else the JSON.</summary>
    private static string DescribeResult(object result)
    {
        try
        {
            var json = result as JsonElement? ?? JsonSerializer.SerializeToElement(result);
            if (json.ValueKind == JsonValueKind.String)
                return json.GetString() ?? string.Empty;

            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var texts = content.EnumerateArray()
                    .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text" && c.TryGetProperty("text", out _))
                    .Select(c => c.GetProperty("text").GetString())
                    .ToList();
                if (texts.Count > 0)
                    return string.Join("\n", texts);
            }

            return JsonSerializer.Serialize(json, s_prettyJson);
        }
        catch (Exception)
        {
            return result.ToString() ?? string.Empty;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "\n…";

    /// <summary>A user bubble's attachments: image thumbnails (data: URLs) and file chips.</summary>
    public static string AttachmentsHtml(IReadOnlyList<Harness.Core.Agent.Attachment> attachments)
    {
        if (attachments.Count == 0)
            return string.Empty;
        var html = new System.Text.StringBuilder("<div class='attachments'>");
        foreach (var attachment in attachments)
        {
            var name = WebUtility.HtmlEncode(attachment.Name);
            html.Append(attachment.Image is { } image
                ? $"<img class='attachment-image' alt='{name}' title='{name}' src='data:{attachment.MediaType};base64,{Convert.ToBase64String(image)}'>"
                : $"<span class='attachment-file' title='{name}'>&#x1F4C4; {name}</span>");
        }
        return html.Append("</div>").ToString();
    }

    /// <summary>The user's text as saved in history: attachments listed by name (their content isn't kept there).</summary>
    public static string WithAttachmentNames(string text, IReadOnlyList<Harness.Core.Agent.Attachment> attachments) =>
        attachments.Count == 0 ? text
        : (text.Length > 0 ? text + "\n\n" : string.Empty) + string.Join("  ", attachments.Select(a => "📎 " + a.Name));

    /// <summary>
    /// An amount with its ISO currency code ("USD 0.0042"): unambiguous where "$" isn't, and with enough
    /// decimals for the fractions of a cent a single call costs.
    /// </summary>
    /// <summary>A "Sources" list for pages a web search cited but the reply doesn't link to.</summary>
    public static string SourcesMarkdown(IReadOnlyList<Harness.Core.Agent.CitationReported> citations)
    {
        var lines = citations.Select((c, i) =>
        {
            var title = string.IsNullOrWhiteSpace(c.Title) ? (Uri.TryCreate(c.Url, UriKind.Absolute, out var u) ? u.Host : c.Url) : c.Title;
            return $"{i + 1}. [{title.Replace("[", "\\[").Replace("]", "\\]")}]({c.Url.Replace(")", "%29")})";
        });
        return $"\n\n**{Strings.Sources}**\n\n" + string.Join('\n', lines);
    }

    public static string FormatCost(decimal amount, string currency)
    {
        var format = amount == 0 ? "0" : amount < 0.01m ? "0.0000" : amount < 1 ? "0.000" : "#,0.00";
        return $"{currency.ToUpperInvariant()} {amount.ToString(format, System.Globalization.CultureInfo.CurrentCulture)}";
    }

    /// <summary>Compact token count: 950, 12.3k, 1.25M.</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(System.Globalization.CultureInfo.CurrentCulture),
        < 1_000_000 => (tokens / 1_000d).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "k",
        _ => (tokens / 1_000_000d).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "M",
    };

    /// <summary>Totals in a few characters: the cost when there is one, otherwise the token count.</summary>
    public static string FormatTotalsShort(Harness.Core.Usage.UsageTotals totals) =>
        totals.Replies == 0 ? string.Empty
        : totals.Cost.Count > 0 ? string.Join(" + ", totals.Cost.Select(c => FormatCost(c.Value, c.Key)))
        : Strings.TokensShort(FormatTokens(totals.InputTokens + totals.OutputTokens));

    public static (string Meta, string Tooltip) FormatMeta(
        string? model, UsageReported? usage, TimeSpan? firstText, TimeSpan total, string? cost = null, string? costTooltip = null)
    {
        var parts = new List<string>();
        var tip = new List<string>();

        if (!string.IsNullOrEmpty(model))
        {
            parts.Add(model);
            tip.Add(Strings.MetaModel(model));
        }

        if (usage is { InputTokens: not null, OutputTokens: not null })
        {
            parts.Add(Strings.TokensInOut(usage.InputTokens.Value, usage.OutputTokens.Value));
            tip.Add(Strings.MetaInput(usage.InputTokens.Value, usage.CachedInputTokens));
            tip.Add(Strings.MetaOutput(usage.OutputTokens.Value, usage.ReasoningTokens));
        }

        if (cost is not null)
            parts.Add(cost);
        if (costTooltip is not null)
            tip.Add(costTooltip);

        parts.Add($"{total.TotalSeconds:F1}s");
        if (firstText is not null)
            tip.Add(Strings.MetaFirstToken(firstText.Value.TotalSeconds));
        tip.Add(Strings.MetaTotal(total.TotalSeconds));

        return (string.Join(" · ", parts), string.Join("\n", tip));
    }
}
