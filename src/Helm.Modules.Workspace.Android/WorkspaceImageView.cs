using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Workspace;

/// <summary>
/// A picture of a prompt (or of the composer's draft): its blob read on a background thread, decoded at the width it
/// is drawn at, and kept for the next time it scrolls by. A picture whose blob is not on the server yet shows a quiet
/// placeholder.
/// </summary>
public sealed class WorkspaceImageView : Border
{
    public static readonly StyledProperty<WorkspaceImage?> ImageProperty =
        AvaloniaProperty.Register<WorkspaceImageView, WorkspaceImage?>(nameof(Image));

    /// <summary>The width it is decoded at (pixels): small for thumbnails, the screen's for the viewer.</summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<WorkspaceImageView, int>(nameof(DecodeWidth), 360);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<WorkspaceImageView, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    /// <summary>Decoded pictures kept, newest last (a phone has little memory: a few dozen thumbnails).</summary>
    private const int CacheSize = 48;
    private static readonly LinkedList<(string Key, Bitmap Bitmap)> Cache = new();

    private readonly Image _image = new();
    private readonly TextBlock _placeholder = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _loads;

    static WorkspaceImageView()
    {
        ImageProperty.Changed.AddClassHandler<WorkspaceImageView>((view, _) => view.Load());
        DecodeWidthProperty.Changed.AddClassHandler<WorkspaceImageView>((view, _) => view.Load());
        StretchProperty.Changed.AddClassHandler<WorkspaceImageView>((view, e) => view._image.Stretch = (Stretch)e.NewValue!);
    }

    public WorkspaceImageView()
    {
        ClipToBounds = true;
        _image.Stretch = Stretch;
        Child = new Panel { Children = { _placeholder, _image } };
    }

    public WorkspaceImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private void Load()
    {
        var load = ++_loads;
        _image.Source = null;
        _placeholder.Foreground = Vs.Get(this, "VsFaint");
        if (Image is not { Blob: { } blob })
        {
            _placeholder.Text = Image is null ? "" : "Picture on its way…";
            return;
        }
        var key = blob.Id + "@" + DecodeWidth;
        if (Cached(key) is { } hit)
        {
            _placeholder.Text = "";
            _image.Source = hit;
            return;
        }
        _placeholder.Text = "…";
        var width = Math.Max(48, DecodeWidth);
        _ = Task.Run(async () =>
        {
            Bitmap? bitmap = null;
            string? problem = null;
            try
            {
                var store = HelmAndroidServices.Current.GetRequiredService<BlobStore>();
                var bytes = await store.ReadAllAsync(blob).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, width);
            }
            catch (Exception ex) when (ex is BlobUnavailableException)
            {
                problem = "Picture not on the server";
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException
                or NotSupportedException or System.Security.Cryptography.CryptographicException or SyncRequestException or TaskCanceledException)
            {
                problem = "Picture could not be loaded";
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (bitmap is not null) Remember(key, bitmap);
                if (load != _loads) return;
                _placeholder.Text = bitmap is null ? problem ?? "" : "";
                _image.Source = bitmap;
            });
        });
    }

    private static Bitmap? Cached(string key)
    {
        for (var node = Cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            Cache.Remove(node);
            Cache.AddLast(node);
            return node.Value.Bitmap;
        }
        return null;
    }

    /// <summary>
    /// The oldest picture leaves the cache when it is full. Its bitmap is not disposed: a view may still show it, and
    /// the collector frees it once none does.
    /// </summary>
    private static void Remember(string key, Bitmap bitmap)
    {
        if (Cached(key) is not null) return;
        Cache.AddLast((key, bitmap));
        while (Cache.Count > CacheSize) Cache.RemoveFirst();
    }
}
