// Harness.WinUI — Licensed under the MIT License.

using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Harness.Core.Agent;

namespace Harness.WinUI;

/// <summary>
/// Images and files sent with a message: pasted into the composer (Ctrl+V of a screenshot or of files
/// copied in Explorer), dropped on the composer, or dropped on the conversation (the chat page reads
/// the file and posts its bytes). They wait in a strip above the composer until the message is sent.
/// </summary>
public sealed partial class MainWindow
{
    private void ConfigureAttachments()
    {
        AttachmentList.ItemsSource = ViewModel.Attachments;
        ViewModel.Attachments.CollectionChanged += (_, _) =>
            AttachmentStrip.Visibility = ViewModel.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A small preview of an image attachment for the strip.</summary>
    public static BitmapImage? Thumbnail(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        var image = new BitmapImage { DecodePixelHeight = 88 };
        try
        {
            using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            image.SetSource(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // An undecodable image still attaches; it just has no preview.
        }
        return image;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Attachment attachment })
            ViewModel.RemoveAttachment(attachment);
        InputTextBox.Focus(FocusState.Programmatic);
    }

    // Text pastes go to the TextBox as usual. Files and images (without text — Excel and Word put a
    // picture of the selection on the clipboard next to the text) become attachments instead.
    private async void InputTextBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // Clipboard busy (another app holds it); let the TextBox try.
        }

        var hasFiles = content.Contains(StandardDataFormats.StorageItems);
        var hasImage = content.Contains(StandardDataFormats.Bitmap) && !content.Contains(StandardDataFormats.Text);
        if (!hasFiles && !hasImage)
            return;

        e.Handled = true;
        await AttachFromAsync(content);
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Bitmap))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Strings.AttachCaption;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void Composer_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await AttachFromAsync(e.DataView);
        }
        finally
        {
            deferral.Complete();
        }
        InputTextBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Attaches the files (or failing that, the bitmap) in a clipboard or drag package.</summary>
    private async Task AttachFromAsync(DataPackageView content)
    {
        try
        {
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                foreach (var item in await content.GetStorageItemsAsync())
                {
                    if (item is IStorageFile file)
                        await AttachAsync(file.Name, () => Attachment.FromFile(file.Path));
                }
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                var bytes = await ReadPngAsync(await content.GetBitmapAsync());
                var name = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png";
                await AttachAsync(name, () => Attachment.FromImage(name, "image/png", bytes));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowHint(Strings.AttachmentFailed(Strings.Attachments, ex.Message), 3000);
        }
    }

    /// <summary>A file the chat page read from a drop on the conversation.</summary>
    private void OnDroppedFile(string name, string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return;
        }
        // Several dropped files arrive as separate messages; keep them in the order they were dropped.
        var previous = _droppedFiles;
        _droppedFiles = AttachAfterAsync();

        async Task AttachAfterAsync()
        {
            await previous;
            await AttachAsync(name, () => Attachment.FromBytes(name, bytes));
        }
    }

    private Task _droppedFiles = Task.CompletedTask;

    /// <summary>Reads the attachment off the UI thread (documents can take a moment), then adds it.</summary>
    private async Task AttachAsync(string name, Func<Attachment> create)
    {
        if (ViewModel.Attachments.Count >= ViewModels.ChatViewModel.MaxAttachments)
        {
            ShowHint(Strings.TooManyAttachments, 2500);
            return;
        }

        try
        {
            var attachment = await Task.Run(create);
            if (!ViewModel.AddAttachment(attachment))
                ShowHint(Strings.TooManyAttachments, 2500);
        }
        catch (AttachmentException ex)
        {
            ShowHint(ex.Problem switch
            {
                AttachmentProblem.TooLarge => Strings.AttachmentTooLarge(name),
                AttachmentProblem.Unsupported => Strings.AttachmentUnsupported(name),
                _ => Strings.AttachmentUnreadable(name),
            }, 3000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowHint(Strings.AttachmentFailed(name, ex.Message), 3000);
        }
    }

    /// <summary>Clipboard bitmaps come in whatever format the source app used (often DIB); send PNG.</summary>
    private static async Task<byte[]> ReadPngAsync(RandomAccessStreamReference reference)
    {
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        using var memory = new MemoryStream();
        output.Seek(0);
        await output.AsStreamForRead().CopyToAsync(memory);
        return memory.ToArray();
    }
}
