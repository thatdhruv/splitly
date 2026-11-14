using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Splitly.Mobile.Services;

/// <summary>Takes or picks a receipt photo and prepares it for upload (JPEG, long side at most 2000 px, comfortably under the 5 MB server limit).</summary>
public static class ReceiptPhoto
{
    private const float MaxSide = 2000f;
    private const long ServerLimit = 5 * 1024 * 1024;

    public static bool CanCapture => MediaPicker.Default.IsCaptureSupported;

    /// <returns>A JPEG stream ready to upload, or null if the user cancelled.</returns>
    public static async Task<Stream?> CaptureAsync()
    {
        var photo = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Scan receipt" });
        return photo is null ? null : await PrepareAsync(photo);
    }

    public static async Task<Stream?> PickAsync()
    {
        var photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Choose a receipt photo", SelectionLimit = 1 });
        return photos.FirstOrDefault() is { } photo ? await PrepareAsync(photo) : null;
    }

    private static async Task<Stream> PrepareAsync(FileResult photo)
    {
        await using var original = await photo.OpenReadAsync();
        var buffer = new MemoryStream();
        await original.CopyToAsync(buffer);
        buffer.Position = 0;

        try
        {
            // Re-encoding also normalises formats the server doesn't accept (e.g. HEIC) to JPEG.
            using Microsoft.Maui.Graphics.IImage image = PlatformImage.FromStream(buffer);
            using Microsoft.Maui.Graphics.IImage small = image.Downsize(MaxSide, MaxSide, disposeOriginal: false);
            var jpeg = new MemoryStream(small.AsBytes(ImageFormat.Jpeg, 0.85f));
            jpeg.Position = 0;
            return jpeg;
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
        {
            // This platform can't re-encode: send the original if the server will take it.
            if (buffer.Length > ServerLimit) throw new InvalidOperationException("That photo is too large to upload (5 MB maximum).");
            buffer.Position = 0;
            return buffer;
        }
    }
}
