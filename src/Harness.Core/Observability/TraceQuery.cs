// Harness.WinUI — Licensed under the MIT License.

using System.Text;

namespace Harness.Core.Observability;

/// <summary>One condition of a search: plain text (the message, a tool or the model), or <c>field:value</c>.</summary>
/// <param name="Field">"" for plain text, or app, model, tool or is.</param>
public sealed record TraceTerm(string Field, string Value, bool Negated);

/// <summary>
/// What the observability search box understands. Words are all required (AND); a leading <c>-</c> excludes; quotes keep words
/// together; <c>field:value</c> narrows:
/// <c>app:name</c> (a pinned app; <c>app:</c> alone is any app), <c>model:</c>, <c>tool:</c>, and <c>is:app</c>, <c>is:chat</c>, <c>is:error</c>.
/// For example <c>app:鼓勵 -timeout model:luna</c>. A word with an unknown field (<c>foo:bar</c>) is searched for as it is.
/// </summary>
public sealed record TraceQuery(IReadOnlyList<TraceTerm> Terms)
{
    public static IReadOnlyList<string> Fields { get; } = ["app", "model", "tool", "is"];

    /// <summary>Values of <c>is:</c>.</summary>
    public static IReadOnlyList<string> IsValues { get; } = ["app", "chat", "error"];

    public bool IsEmpty => Terms.Count == 0;

    public static TraceQuery Parse(string? text)
    {
        List<TraceTerm> terms = [];
        foreach (var token in Split(text ?? string.Empty))
        {
            var negated = token.Length > 1 && token[0] == '-';
            var body = negated ? token[1..] : token;
            var field = string.Empty;
            var colon = body.IndexOf(':');
            if (colon > 0 && Fields.Contains(body[..colon], StringComparer.OrdinalIgnoreCase))
            {
                field = body[..colon].ToLowerInvariant();
                body = body[(colon + 1)..];
            }

            body = body.Trim();
            // "app:" alone means any app; for the others an empty value says nothing.
            if (body.Length == 0 && field != "app")
                continue;
            if (field == "is" && !IsValues.Contains(body, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(new TraceTerm(string.Empty, "is:" + body, negated)); // not a known state: look for the text
                continue;
            }

            terms.Add(new TraceTerm(field, field == "is" ? body.ToLowerInvariant() : body, negated));
        }

        return new TraceQuery(terms);
    }

    /// <summary>The text as the search box shows it again (used to add a condition to what the person typed).</summary>
    public static string Join(string? text, string condition) =>
        string.IsNullOrWhiteSpace(text) ? condition : text.Trim() + " " + condition;

    /// <summary>Words split at spaces; a quoted run stays together (its quotes are dropped).</summary>
    private static IEnumerable<string> Split(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
