// Harness.WinUI — Licensed under the MIT License.

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Images;
using ImageGenerationOptions = OpenAI.Images.ImageGenerationOptions;

namespace Harness.Core.Tools;

/// <summary>Image model on the same OpenAI-compatible endpoint/key as chat (e.g. Azure OpenAI gpt-image-2).</summary>
public sealed record ImageGenerationSettings(string Endpoint, string ApiKey, string Model);

public static class ImageGenerationTool
{
    public const string Name = "generate_image";

    public static string OutputDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Harness.WinUI");

    /// <summary>
    /// Generates a PNG into <see cref="OutputDirectory"/> and returns its path; the UI shows the image
    /// from the tool result, so the model doesn't need to (and is told not to) embed it again.
    /// </summary>
    /// <param name="defaultQuality">The user's quality setting, read at call time; used unless the model passes one.</param>
    public static AIFunction Create(ImageGenerationSettings settings, Func<string> defaultQuality) => AIFunctionFactory.Create(
        async (
            [Description("Detailed description of the image. English usually works best; put any text that must appear in the image in quotes.")] string prompt,
            [Description("\"1024x1024\" (square), \"1536x1024\" (landscape), \"1024x1536\" (portrait) or \"auto\".")] string size,
            CancellationToken cancellationToken,
            [Description("Leave empty to use the user's quality setting. Only set it (\"low\" ~20s, \"medium\" ~50s, \"high\" ~2 min) when the user explicitly asks for a quality in their message.")] string? quality = null) =>
        {
            quality = NormalizeQuality(string.IsNullOrWhiteSpace(quality) ? defaultQuality() : quality);
            var client = new ImageClient(settings.Model, new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(settings.Endpoint),
                    NetworkTimeout = TimeSpan.FromMinutes(5),
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(Observability.Telemetry.HttpClient),
                });

            var options = new ImageGenerationOptions { Size = ParseSize(size), Quality = ParseQuality(quality) };
            var image = (await client.GenerateImageAsync(prompt, options, cancellationToken).ConfigureAwait(false)).Value;
            var bytes = image.ImageBytes ?? throw new InvalidOperationException("The image model returned no image data.");

            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, $"harness-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}.png");
            await File.WriteAllBytesAsync(path, bytes.ToArray(), cancellationToken).ConfigureAwait(false);

            return new
            {
                file = path,
                quality,
                revised_prompt = image.RevisedPrompt,
                note = "The image is already shown to the user in the chat. Do not embed or link it again; briefly describe it or ask if they want changes.",
            };
        },
        Name,
        "Generate an image from a text prompt with the configured image model. Saves a PNG to the user's Pictures\\Harness.WinUI folder and shows it in the chat.");

    private static GeneratedImageSize? ParseSize(string? size)
    {
        var parts = size?.ToLowerInvariant().Split('x');
        return parts is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height)
            ? new GeneratedImageSize(width, height)
            : null;
    }

    /// <summary>
    /// Normalized quality the tool will use. Anything unrecognized — including "auto", which gpt-image-2
    /// treats as high (~2 minutes for a 1024px image) — becomes "low" (~20s).
    /// </summary>
    public static string NormalizeQuality(string? quality) => quality?.Trim().ToLowerInvariant() switch
    {
        "medium" => "medium",
        "high" => "high",
        _ => "low",
    };

    // gpt-image quality levels are still flagged experimental (OPENAI001) in OpenAI 2.x.
#pragma warning disable OPENAI001
    private static GeneratedImageQuality ParseQuality(string? quality) => NormalizeQuality(quality) switch
    {
        "medium" => GeneratedImageQuality.MediumQuality,
        "high" => GeneratedImageQuality.HighQuality,
        _ => GeneratedImageQuality.LowQuality,
    };
#pragma warning restore OPENAI001
}
