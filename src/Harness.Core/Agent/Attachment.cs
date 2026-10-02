// Harness.WinUI — Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.AI;
using Harness.Core.Files;

namespace Harness.Core.Agent;

/// <summary>
/// Something the user attached to a message (pasted or dropped): an image the model sees directly, or a
/// file whose text is included with the message (Office and PDF via <see cref="DocumentText"/>, plain
/// text and source files as they are).
/// </summary>
/// <param name="Image">PNG/JPEG/GIF/WebP bytes for an image attachment; null for text.</param>
/// <param name="Text">The file's text for a text attachment; null for an image.</param>
public sealed record Attachment(string Name, string MediaType, byte[]? Image, string? Text)
{
    public const int MaxImageBytes = 10 * 1024 * 1024;
    public const int MaxTextChars = 60_000;

    private static readonly Dictionary<string, string> s_imageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    public bool IsImage => Image is not null;

    /// <summary>An image (e.g. a pasted screenshot).</summary>
    public static Attachment FromImage(string name, string mediaType, byte[] bytes) =>
        bytes.Length > MaxImageBytes
            ? throw new AttachmentException(AttachmentProblem.TooLarge, $"{name} is larger than {MaxImageBytes / 1024 / 1024} MB.")
            : new Attachment(name, mediaType, bytes, null);

    /// <summary>A file from disk: an image, an Office/PDF document, or text. Throws <see cref="AttachmentException"/> if it can't be used.</summary>
    public static Attachment FromFile(string path)
    {
        var name = Path.GetFileName(path);
        if (s_imageTypes.TryGetValue(Path.GetExtension(path), out var imageType))
            return FromImage(name, imageType, File.ReadAllBytes(path));

        if (DocumentText.IsDocument(path))
        {
            try
            {
                return new Attachment(name, "text/plain", null, DocumentText.Extract(path, MaxTextChars));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new AttachmentException(AttachmentProblem.Unreadable, $"Couldn't read {name}: {ex.Message}");
            }
        }

        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
            throw new AttachmentException(AttachmentProblem.TooLarge, $"{name} is too large to attach as text.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            throw new AttachmentException(AttachmentProblem.Unsupported, $"{name} isn't a text, image or Office/PDF file.");
        var text = new UTF8Encoding(false).GetString(bytes);
        if (text.Length > MaxTextChars)
            text = text[..MaxTextChars] + $"\n…(truncated at {MaxTextChars:N0} characters)";
        return new Attachment(name, "text/plain", null, text);
    }

    /// <summary>A file's bytes from a drop (written to a temporary file so documents can be read like any other).</summary>
    public static Attachment FromBytes(string name, byte[] bytes)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Harness.WinUI", "drops", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(name));
        try
        {
            File.WriteAllBytes(path, bytes);
            return FromFile(path);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The message the model gets: the user's text, each text attachment as a labelled block, each image as image content.</summary>
    public static ChatMessage BuildMessage(string text, IReadOnlyList<Attachment> attachments)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(text))
            contents.Add(new TextContent(text));
        foreach (var attachment in attachments)
        {
            contents.Add(attachment.Image is { } image
                ? new DataContent(image, attachment.MediaType) { Name = attachment.Name }
                : new TextContent($"<attachment name=\"{attachment.Name}\">\n{attachment.Text}\n</attachment>"));
        }
        return new ChatMessage(ChatRole.User, contents);
    }
}

public enum AttachmentProblem { TooLarge, Unsupported, Unreadable }

public sealed class AttachmentException(AttachmentProblem problem, string message) : Exception(message)
{
    public AttachmentProblem Problem { get; } = problem;
}
