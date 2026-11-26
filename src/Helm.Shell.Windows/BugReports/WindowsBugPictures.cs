using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Helm.BugReports.Ui;
using Helm.Shell.BugReports;

namespace Helm.Shell.Windows;

/// <summary>Where a bug report's pictures come from on this PC (WPF's clipboard and file dialog).</summary>
internal static class WindowsBugPictures
{
    /// <summary>Pictures on the clipboard: a copied image (as PNG) or copied picture files.</summary>
    public static IReadOnlyList<BugImageDraft> FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                return FromFiles(Clipboard.GetFileDropList().Cast<string>());
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } source)
                return [new BugImageDraft(Png(source), "image/png", $"Pasted {DateTime.Now:HH-mm-ss}.png")];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy: nothing pasted.
        }
        return [];
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
    public static IReadOnlyList<BugImageDraft> Pick(int max)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add images",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*",
            Multiselect = true,
        };
        return dialog.ShowDialog() == true ? FromFiles(dialog.FileNames).Take(Math.Max(0, max)).ToList() : [];
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

    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}
