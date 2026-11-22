using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Helm.Core.Diagnostics;
using Helm.Shell.BugReports;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;

namespace Helm.App.BugReports;

/// <summary>
/// The colours and icons of bug reports, from the theme's own brushes (as Tracker's "Urgent" tag): a status, a severity
/// or a black box event in, a brush or a symbol out.
/// </summary>
internal sealed class BugLookConverter : IValueConverter
{
    /// <summary>"brush" or "icon".</summary>
    public string What { get; set; } = "brush";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = What == "icon";
        var (symbol, brush) = value switch
        {
            BugStatus.Open => (SymbolRegular.Bug24, "AccentTextFillColorPrimaryBrush"),
            BugStatus.Fixing => (SymbolRegular.Bot24, "AccentTextFillColorPrimaryBrush"),
            BugStatus.ToVerify => (SymbolRegular.Clock24, "SystemFillColorCautionBrush"),
            BugStatus.Closed => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            BugSeverity.Blocking => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            BugSeverity.Annoying => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            BugSeverity.Minor => (SymbolRegular.Info24, "TextFillColorSecondaryBrush"),
            BlackBoxKind.Input => (SymbolRegular.Keyboard24, "AccentTextFillColorPrimaryBrush"),
            BlackBoxKind.Navigation => (SymbolRegular.Window24, "TextFillColorSecondaryBrush"),
            BlackBoxKind.Log => (SymbolRegular.DocumentText24, "TextFillColorSecondaryBrush"),
            BlackBoxKind.Error => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            BlackBoxKind.Performance => (SymbolRegular.Gauge24, "SystemFillColorCautionBrush"),
            BlackBoxKind.Sync => (SymbolRegular.ArrowSync24, "TextFillColorSecondaryBrush"),
            BlackBoxKind.DisabledClick => (SymbolRegular.LockClosed24, "SystemFillColorCautionBrush"),
            _ => (SymbolRegular.Info24, "TextFillColorSecondaryBrush"),
        };
        if (icon) return symbol;
        return Application.Current?.TryFindResource(brush) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// <c>bugs:BugImage.Item="{Binding}"</c> on an <see cref="Image"/>: shows a <see cref="BugImageItem"/> once its bytes are
/// read (a saved picture may come from the sync server), decoded small for a thumbnail.
/// </summary>
internal static class BugImage
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(BugImageItem), typeof(BugImage), new PropertyMetadata(null, (d, e) => _ = LoadAsync((Image)d, e.NewValue as BugImageItem)));

    /// <summary>Decode width in pixels (0: full size, for the large view).</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(BugImage), new PropertyMetadata(240));

    public static BugImageItem? GetItem(DependencyObject d) => (BugImageItem?)d.GetValue(ItemProperty);

    public static void SetItem(DependencyObject d, BugImageItem? value) => d.SetValue(ItemProperty, value);

    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

    private static async Task LoadAsync(Image image, BugImageItem? item)
    {
        image.Source = null;
        if (item is null) return;
        try
        {
            var bytes = await item.LoadAsync().ConfigureAwait(true);
            if (GetItem(image) != item) return;
            image.Source = Decode(bytes, GetDecodeWidth(image));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException
            or Helm.Core.Sync.BlobUnavailableException or System.Net.Http.HttpRequestException or FileFormatException)
        {
            image.ToolTip = $"This picture cannot be shown now: {ex.Message}";
        }
    }

    public static BitmapImage Decode(byte[] bytes, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Opens a picture full size in the default viewer (a temp copy).</summary>
    public static async Task OpenAsync(BugImageItem item)
    {
        var bytes = await item.LoadAsync().ConfigureAwait(true);
        var folder = Path.Combine(Path.GetTempPath(), "helm-bugs");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}{ExtensionOf(bytes)}");
        await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(true);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static string ExtensionOf(byte[] b) =>
        b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 ? ".png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg"
        : b.Length > 3 && b[0] == 0x47 && b[1] == 0x49 ? ".gif"
        : b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D ? ".bmp"
        : ".png";

    /// <summary>A picture's media type from its first bytes (null: not a picture we take).</summary>
    public static string? MimeOf(byte[] b) =>
        b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg"
        : b.Length > 3 && b[0] == 0x47 && b[1] == 0x49 ? "image/gif"
        : b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D ? "image/bmp"
        : b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50 ? "image/webp"
        : null;

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
                if (!info.Exists || info.Length > Shell.BugReports.BugStore.MaxImageBytes * 2L) continue;
                var bytes = File.ReadAllBytes(path);
                if (MimeOf(bytes) is { } mime) images.Add(new BugImageDraft(bytes, mime, info.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip what cannot be read.
            }
        }
        return images;
    }

    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    /// <summary>Asks for picture files (Add image).</summary>
    public static IReadOnlyList<BugImageDraft> Pick(Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add images",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*",
            Multiselect = true,
        };
        return dialog.ShowDialog(owner) == true ? FromFiles(dialog.FileNames) : [];
    }
}
