using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using FluentIcons.Common;
using Helm.Core.Diagnostics;
using Helm.Shell.BugReports;

namespace Helm.App.Android.BugReports;

/// <summary>
/// The colours and icons of bug reports from Helm's own brushes (HelmDanger, HelmSuccess, the accent): a status, a
/// severity or a black box event in, a symbol (<see cref="Icon"/>) or a brush (<see cref="Brush"/>) out.
/// </summary>
public static class BugLook
{
    public static IValueConverter Icon { get; } = new FuncValueConverter<object?, Symbol>(value => value switch
    {
        BugStatus.Open => Symbol.Bug,
        BugStatus.Fixing => Symbol.Bot,
        BugStatus.ToVerify => Symbol.Clock,
        BugStatus.Closed => Symbol.CheckmarkCircle,
        BlackBoxKind.Input => Symbol.Keyboard,
        BlackBoxKind.Navigation => Symbol.Window,
        BlackBoxKind.Log => Symbol.DocumentText,
        BlackBoxKind.Error => Symbol.ErrorCircle,
        BlackBoxKind.Performance => Symbol.Gauge,
        BlackBoxKind.Sync => Symbol.ArrowSync,
        BlackBoxKind.DisabledClick => Symbol.LockClosed,
        _ => Symbol.Info,
    });

    public static IValueConverter Brush { get; } = new FuncValueConverter<object?, IBrush?>(value => value switch
    {
        BugStatus.Open or BugStatus.Fixing or BlackBoxKind.Input => Accent(),
        BugStatus.ToVerify or BugSeverity.Annoying or BlackBoxKind.Performance or BlackBoxKind.DisabledClick => Caution(),
        BugStatus.Closed => Resource("HelmSuccess"),
        BugSeverity.Blocking or BlackBoxKind.Error => Resource("HelmDanger"),
        _ => Resource("HelmSecondaryText"),
    });

    private static IBrush? Resource(string key) =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;

    private static IBrush Accent() =>
        Application.Current?.TryFindResource("SystemAccentColor", Application.Current.ActualThemeVariant, out var value) == true && value is Color c
            ? new SolidColorBrush(c) : Brushes.DodgerBlue;

    private static IBrush Caution() =>
        new SolidColorBrush(Application.Current?.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#9D5D00") : Color.Parse("#FCE100"));
}

/// <summary>
/// <c>b:BugImageLoader.Item="{Binding}"</c> on an <see cref="Image"/>: shows a <see cref="BugImageItem"/> once its bytes
/// are read (a saved picture may come from the sync server), decoded small.
/// </summary>
public sealed class BugImageLoader : AvaloniaObject
{
    public static readonly AttachedProperty<BugImageItem?> ItemProperty =
        AvaloniaProperty.RegisterAttached<BugImageLoader, Image, BugImageItem?>("Item");

    static BugImageLoader() => ItemProperty.Changed.AddClassHandler<Image>((image, e) => _ = LoadAsync(image, e.NewValue as BugImageItem));

    public static BugImageItem? GetItem(Image image) => image.GetValue(ItemProperty);

    public static void SetItem(Image image, BugImageItem? value) => image.SetValue(ItemProperty, value);

    private static async Task LoadAsync(Image image, BugImageItem? item)
    {
        image.Source = null;
        if (item is null) return;
        try
        {
            var bytes = await item.LoadAsync().ConfigureAwait(true);
            if (GetItem(image) != item) return;
            using var stream = new MemoryStream(bytes);
            image.Source = Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception)
        {
            // Not readable now (not synced yet, damaged): the frame stays empty.
        }
    }
}
