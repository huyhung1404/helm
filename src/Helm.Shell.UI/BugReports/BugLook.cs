using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using FluentIcons.Common;
using Helm.Core.Diagnostics;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>
/// The icons and colours of bug reports: a status, a severity or a black box event in, a symbol (<see cref="Icon"/>) or a
/// colour (<c>b:BugLook.Tone="{Binding Status}"</c>, from Helm's theme keys) out. The tone is a resource binding on the
/// control, so it follows the light or dark theme of the page it is on (a Gallery frame may differ from the window's).
/// </summary>
public sealed class BugLook : AvaloniaObject
{
    /// <summary>True for a count above zero.</summary>
    public static IValueConverter Any { get; } = new FuncValueConverter<int, bool>(n => n > 0);

    public static IValueConverter Icon { get; } = new FuncValueConverter<object?, Symbol>(value => value switch
    {
        BugStatus.Open => Symbol.Bug,
        BugStatus.Fixing => Symbol.Bot,
        BugStatus.ToVerify => Symbol.Clock,
        BugStatus.Closed => Symbol.CheckmarkCircle,
        BugSeverity.Blocking => Symbol.ErrorCircle,
        BugSeverity.Annoying => Symbol.Warning,
        BugSeverity.Minor => Symbol.Info,
        BlackBoxKind.Input => Symbol.Keyboard,
        BlackBoxKind.Navigation => Symbol.Window,
        BlackBoxKind.Log => Symbol.DocumentText,
        BlackBoxKind.Error => Symbol.ErrorCircle,
        BlackBoxKind.Performance => Symbol.Gauge,
        BlackBoxKind.Sync => Symbol.ArrowSync,
        BlackBoxKind.DisabledClick => Symbol.LockClosed,
        _ => Symbol.Info,
    });

    /// <summary>The theme key of a status, severity or event kind.</summary>
    public static string ToneKey(object? value) => value switch
    {
        BugStatus.Open or BugStatus.Fixing or BlackBoxKind.Input => "HelmEyebrow",
        BugStatus.ToVerify or BugSeverity.Annoying or BlackBoxKind.Performance or BlackBoxKind.DisabledClick => "HelmCaution",
        BugStatus.Closed => "HelmSuccess",
        BugSeverity.Blocking or BlackBoxKind.Error => "HelmDanger",
        _ => "HelmSecondaryText",
    };

    public static readonly AttachedProperty<object?> ToneProperty =
        AvaloniaProperty.RegisterAttached<BugLook, Control, object?>("Tone");

    static BugLook() => ToneProperty.Changed.AddClassHandler<Control>((control, e) =>
        // Foreground is inherited: the icon and the text inside a tag both take it.
        control.Bind(TextElement.ForegroundProperty, control.GetResourceObservable(ToneKey(e.NewValue))));

    public static object? GetTone(Control control) => control.GetValue(ToneProperty);

    public static void SetTone(Control control, object? value) => control.SetValue(ToneProperty, value);
}

/// <summary>
/// <c>b:BugImageLoader.Item="{Binding}"</c> on an <see cref="Image"/>: shows a <see cref="BugImageItem"/> once its bytes
/// are read (a saved picture may come from the sync server), decoded to <c>DecodeWidth</c> (320 for a thumbnail).
/// </summary>
public sealed class BugImageLoader : AvaloniaObject
{
    public static readonly AttachedProperty<BugImageItem?> ItemProperty =
        AvaloniaProperty.RegisterAttached<BugImageLoader, Image, BugImageItem?>("Item");

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<BugImageLoader, Image, int>("DecodeWidth", 320);

    static BugImageLoader() => ItemProperty.Changed.AddClassHandler<Image>((image, e) => _ = LoadAsync(image, e.NewValue as BugImageItem));

    public static BugImageItem? GetItem(Image image) => image.GetValue(ItemProperty);

    public static void SetItem(Image image, BugImageItem? value) => image.SetValue(ItemProperty, value);

    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);

    private static async Task LoadAsync(Image image, BugImageItem? item)
    {
        image.Source = null;
        if (item is null) return;
        try
        {
            var bytes = await item.LoadAsync().ConfigureAwait(true);
            if (GetItem(image) != item) return;
            using var stream = new MemoryStream(bytes);
            image.Source = Bitmap.DecodeToWidth(stream, GetDecodeWidth(image));
        }
        catch (Exception ex)
        {
            // Not readable now (not synced yet, damaged): the frame stays empty and says why.
            ToolTip.SetTip(image, $"This picture cannot be shown now: {ex.Message}");
        }
    }
}

/// <summary>Pictures from files (a drop on the report form): what each one is, from its first bytes.</summary>
public static class BugPictureFiles
{
    /// <summary>A picture's media type from its first bytes (null: not a picture Helm takes).</summary>
    public static string? MimeOf(byte[] b) =>
        b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg"
        : b.Length > 3 && b[0] == 0x47 && b[1] == 0x49 ? "image/gif"
        : b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D ? "image/bmp"
        : b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50 ? "image/webp"
        : null;

    /// <summary>The bytes as a picture to add, or null when they are not one.</summary>
    public static BugImageDraft? Draft(byte[] bytes, string? name) =>
        MimeOf(bytes) is { } mime ? new BugImageDraft(bytes, mime, name) : null;
}
