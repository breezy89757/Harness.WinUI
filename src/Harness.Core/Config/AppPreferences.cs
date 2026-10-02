// Harness.WinUI — Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Harness.Core.Config;

/// <summary>Non-secret app preferences, stored in preferences.json in the data folder (<see cref="AppPaths"/>).</summary>
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

    /// <summary>Text size of the conversation and composer, in percent (80-200).</summary>
    public int TextZoom { get; init; } = 100;

    /// <summary>ISO 4217 code the model prices are in (and costs are shown in).</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Prices per million tokens by model name; a model without one shows tokens only.</summary>
    public Dictionary<string, Usage.ModelPrice>? ModelPrices { get; init; }

    /// <summary>Skills (by name) the user turned off; every other discovered skill is offered to the model.</summary>
    public List<string>? DisabledSkills { get; init; }

    /// <summary>The price set for <paramref name="model"/> (names compare case-insensitively), or null.</summary>
    public Usage.ModelPrice? PriceFor(string? model) =>
        model is null ? null : ModelPrices?.FirstOrDefault(p => string.Equals(p.Key, model, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The effort to send with each turn; null for "auto" or anything unrecognized.</summary>
    public static ReasoningEffort? ParseEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
        "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
        "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
        _ => null,
    };

    public static string DefaultSandboxFolder => AppPaths.Combine("sandbox");

    private static string FilePath => AppPaths.Combine("preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            var preferences = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new AppPreferences()
                : new AppPreferences();
            // A sandbox saved inside the pre-rename data folder moved along with it.
            return preferences with { SandboxFolder = AppPaths.MapLegacyPath(preferences.SandboxFolder) };
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
