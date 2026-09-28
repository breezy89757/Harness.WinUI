// Yoke — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Yoke.Core.Config;

/// <summary>Non-secret app preferences, stored in %LOCALAPPDATA%\Yoke\preferences.json.</summary>
public sealed record AppPreferences
{
    public const string SystemLanguage = "system";
    public const string English = "en";
    public const string TraditionalChinese = "zh-TW";

    /// <summary><see cref="SystemLanguage"/>, <see cref="English"/> or <see cref="TraditionalChinese"/>.</summary>
    public string Language { get; init; } = SystemLanguage;

    /// <summary>
    /// Folder the built-in file tools may access. Null = never chosen (first run picks a default);
    /// empty = file tools turned off.
    /// </summary>
    public string? SandboxFolder { get; init; }

    /// <summary>"auto" (the model's default), "low", "medium" or "high".</summary>
    public string ReasoningEffort { get; init; } = "auto";

    /// <summary>Default generate_image quality: "low" (~20s), "medium" (~50s) or "high" (~2 min).</summary>
    public string ImageQuality { get; init; } = "low";

    /// <summary>The effort to send with each turn; null for "auto" or anything unrecognized.</summary>
    public static ReasoningEffort? ParseEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
        "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
        "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
        _ => null,
    };

    public static string DefaultSandboxFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke", "sandbox");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yoke", "preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new AppPreferences()
                : new AppPreferences();
        }
        catch (JsonException)
        {
            return new AppPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
