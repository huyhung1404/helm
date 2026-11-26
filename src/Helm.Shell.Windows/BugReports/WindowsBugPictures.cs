using System.IO;
using Avalonia.Platform.Storage;
using Helm.Core.Desktop;
using Helm.Ui.Windows;
using Helm.BugReports.Ui;
using Helm.Shell.BugReports;

namespace Helm.Shell.Windows;

/// <summary>Where a bug report's pictures come from on this PC (the Windows clipboard and the window's file picker).</summary>
internal static class WindowsBugPictures
{
    /// <summary>Pictures on the clipboard: a copied image (as PNG) or copied picture files.</summary>
    public static IReadOnlyList<BugImageDraft> FromClipboard()
    {
        if (WindowsClipboard.GetFiles() is { Count: > 0 } files) return FromFiles(files);
        if (WindowsClipboard.GetImage() is not { } bytes) return [];
        // A BMP from the clipboard becomes a PNG (smaller, and what the report keeps).
        var png = BugPictureFiles.MimeOf(bytes) == "image/png" ? bytes : Png(bytes);
        return png is null ? [] : [new BugImageDraft(png, "image/png", $"Pasted {DateTime.Now:HH-mm-ss}.png")];
    }

    /// <summary>The picture files among <paramref name="paths"/>.</summary>
    public static IReadOnlyList<BugImageDraft> FromFiles(IEnumerable<string> paths)
    {
        var images = new List<BugImageDraft>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > BugStore.MaxImageBytes * 2L) continue;
                if (BugPictureFiles.Draft(File.ReadAllBytes(path), info.Name) is { } draft) images.Add(draft);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip what cannot be read.
            }
        }
        return images;
    }

    /// <summary>Asks for picture files (at most <paramref name="max"/>); the dialog belongs to the window in front.</summary>
    public static async Task<IReadOnlyList<BugImageDraft>> PickAsync(int max)
    {
        var images = new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] };
        var paths = await DesktopWindows.PickFilesAsync("Add images", [images, FilePickerFileTypes.All], many: true).ConfigureAwait(true);
        return FromFiles(paths).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>Opens a picture full size in the default viewer (a temp copy).</summary>
    public static async Task OpenAsync(byte[] bytes)
    {
        var folder = Path.Combine(Path.GetTempPath(), "helm-bugs");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}{ExtensionOf(bytes)}");
        await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(true);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static string ExtensionOf(byte[] b) => BugPictureFiles.MimeOf(b) switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private static byte[]? Png(byte[] image)
    {
        try
        {
            using var input = new MemoryStream(image);
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(input);
            using var output = new MemoryStream();
            bitmap.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
