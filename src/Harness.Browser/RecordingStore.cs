// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.Config;

namespace Harness.Browser;

/// <summary>Where recordings and run results are kept: the app's data folder (browser-recordings, browser-runs), a name that says what the case is and when it was made.</summary>
public static class RecordingStore
{
    public static string RecordingsFolder => AppPaths.Combine("browser-recordings");

    public static string RunsFolder => AppPaths.Combine("browser-runs");

    /// <summary>Writes the recording as JSON and returns the path: <c>{name}-{yyyyMMdd-HHmmss}.json</c>.</summary>
    public static string Save(Recording recording, string? folder = null)
    {
        folder ??= RecordingsFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{recording.Name}-{recording.RecordedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, recording.ToJson());
        return path;
    }

    public static Recording? Load(string path) => File.Exists(path) ? Recording.FromJson(File.ReadAllText(path)) : null;

    /// <summary>The recordings in the folder, newest first.</summary>
    public static IReadOnlyList<string> List(string? folder = null)
    {
        folder ??= RecordingsFolder;
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.json").OrderByDescending(File.GetLastWriteTimeUtc)] : [];
    }

    /// <summary>Writes a run result as JSON and a Markdown summary next to each other; returns the path of the Markdown file.</summary>
    public static string SaveRun(RunResult result, string? folder = null)
    {
        folder ??= RunsFolder;
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, $"{result.Name}-{result.Mode}-{result.StartedAt:yyyyMMdd-HHmmss}");
        File.WriteAllText(stem + ".json", result.ToJson());
        File.WriteAllText(stem + ".md", result.ToMarkdown());
        return stem + ".md";
    }
}
