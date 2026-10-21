// Harness.WinUI — Licensed under the MIT License.

using System.Text;

namespace Harness.Core.Files;

/// <summary>A small line diff (longest common subsequence) for showing what an edit changes: "-" removed, "+" added, " " context.</summary>
public static class TextDiff
{
    private const int MaxLines = 1500;

    /// <param name="context">Unchanged lines to keep around each change.</param>
    /// <param name="maxOutputLines">Cut the diff here (with a note); 0 for no limit.</param>
    public static string Unified(string oldText, string newText, int context = 2, int maxOutputLines = 60)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        if (a.Length > MaxLines || b.Length > MaxLines)
            return $"(too large to compare line by line: {a.Length} lines became {b.Length})";

        // Trim the common head and tail so the table is only as big as what changed.
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
            head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
            tail++;
        var x = a[head..(a.Length - tail)];
        var y = b[head..(b.Length - tail)];

        var table = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
        {
            for (var j = y.Length - 1; j >= 0; j--)
                table[i, j] = x[i] == y[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        }

        // The whole file as marked lines, then keep only the changes with their context.
        var marked = new List<(char Mark, string Text, int Line)>();
        for (var i = 0; i < head; i++)
            marked.Add((' ', a[i], i + 1));
        int p = 0, q = 0;
        while (p < x.Length || q < y.Length)
        {
            if (p < x.Length && q < y.Length && x[p] == y[q])
            {
                marked.Add((' ', x[p], head + p + 1));
                p++;
                q++;
            }
            else if (p < x.Length && (q == y.Length || table[p + 1, q] >= table[p, q + 1]))
            {
                // Removals first, so a changed line reads as "-old" then "+new".
                marked.Add(('-', x[p], head + p + 1));
                p++;
            }
            else
            {
                marked.Add(('+', y[q], head + q + 1));
                q++;
            }
        }
        for (var i = a.Length - tail; i < a.Length; i++)
            marked.Add((' ', a[i], i + 1));

        var keep = new bool[marked.Count];
        for (var i = 0; i < marked.Count; i++)
        {
            if (marked[i].Mark == ' ')
                continue;
            for (var k = Math.Max(0, i - context); k <= Math.Min(marked.Count - 1, i + context); k++)
                keep[k] = true;
        }

        var output = new StringBuilder();
        var shown = 0;
        var gap = false;
        for (var i = 0; i < marked.Count; i++)
        {
            if (!keep[i])
            {
                gap = shown > 0;
                continue;
            }
            if (gap)
            {
                output.Append("  …\n");
                gap = false;
            }
            if (maxOutputLines > 0 && shown >= maxOutputLines)
            {
                output.Append($"  … ({marked.Skip(i).Count(m => m.Mark != ' ')} more changed lines)\n");
                break;
            }
            output.Append(marked[i].Mark).Append(' ').Append(marked[i].Text).Append('\n'); // "\n", not the platform's: this is shown in a page
            shown++;
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>How many lines a diff added and removed.</summary>
    public static (int Added, int Removed) Count(string oldText, string newText)
    {
        var unified = Unified(oldText, newText, context: 0, maxOutputLines: 0);
        var added = 0;
        var removed = 0;
        foreach (var line in unified.Split('\n'))
        {
            if (line.StartsWith("+ ", StringComparison.Ordinal))
                added++;
            else if (line.StartsWith("- ", StringComparison.Ordinal))
                removed++;
        }
        return (added, removed);
    }

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}
