using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Scratch;
using Helm.Modules.Scratch.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Scratch a Gallery entry shows.</summary>
internal enum ScratchStage
{
    /// <summary>A wall of pictures, files and texts.</summary>
    Wall,
    /// <summary>Nothing in it yet.</summary>
    Empty,
}

/// <summary>The real Scratch view over an in-memory store: pictures drawn here, a few files and texts.</summary>
internal static class ScratchDemo
{
    public static Control Create(FakeModule module, ScratchStage stage, bool detail = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var paths = new HelmPaths(dir);
        Directory.CreateDirectory(paths.SyncDirectory);
        var db = new SyncDatabase(paths.SyncDatabaseFile, Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var time = new DemoTime();
        var store = new ScratchStore(new MemorySynced<ScratchItem>(), blobs, time);
        string? firstText = null;
        string? picture = null;
        if (stage == ScratchStage.Wall)
        {
            Seed(store, time, out firstText, out picture);
        }

        var settings = new SettingsStoreFactory(paths);
        var platform = new FakePlatform(Path.Combine(dir, "open"));
        var device = new Device();
        var ui = new AvaloniaUi();
        var relay = new ScratchClipboardRelay(new MemorySynced<ScratchClipboardSignal>(), store, platform, new NoClipboard(), settings, device, ui);
        var vm = new ScratchViewModel(store, new ScratchImporter(store, platform, device), relay, platform, settings, ui, new YesDialogs(), new NoClipboard(),
            NullLogger<ScratchViewModel>.Instance);
        var view = new ScratchView(module, vm, new Host());
        if (detail && picture is not null && vm.Rows.FirstOrDefault(r => r.Name == picture) is { } row) vm.SelectCommand.Execute(row);
        return view;
    }

    private static void Seed(ScratchStore store, DemoTime time, out string firstText, out string picture)
    {
        picture = "IMG_4821.jpg";
        time.Start();
        Add(store, time, picture, "image/jpeg", Photo(360, 270, "#0077F2", "#00CFC0"), 3_400_000);
        Add(store, time, "Screenshot 2026-11-24.png", "image/png", Photo(360, 220, "#7A5AF8", "#06AEFF"), 820_000);
        Add(store, time, "VID_0233.mp4", "video/mp4", Photo(360, 202, "#F5A524", "#E5484D"), 6_200_000);
        Add(store, time, "Hoa-don-dien-T11.pdf", "application/pdf", null, 210_000);
        Add(store, time, "bao-cao-tuan-47.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, 96_000);
        Add(store, time, "Scan CCCD.jpg", "image/jpeg", Photo(360, 240, "#30A46C", "#00C6E0"), 1_900_000);
        firstText = "https://helm.huyhung1404.com/download";
        time.Advance();
        store.AddTextAsync(firstText, "Pixel 8").GetAwaiter().GetResult();
        time.Advance();
        store.AddTextAsync("Wifi quán cà phê: HighlandsGuest / mật khẩu: 12345678", "Pixel 8").GetAwaiter().GetResult();
        time.Advance();
        store.AddTextAsync("Địa chỉ nhà ông bà:\n12 ngõ 45 Trần Duy Hưng\nCầu Giấy, Hà Nội", "Work PC").GetAwaiter().GetResult();
        time.Advance();
        store.AddTextAsync("ssh hung@192.168.1.20 -p 2222", "Home PC").GetAwaiter().GetResult();
        time.Advance();
        store.AddTextAsync("Mã giảm giá: HELM2026", "Pixel 8").GetAwaiter().GetResult();
        time.Advance();
        var gone = store.AddTextAsync("Ghi chú cũ đã xoá", "Work PC").GetAwaiter().GetResult();
        store.MoveToTrash(gone);
        store.MoveToTrash(store.AddTextAsync("Link tạm", "Pixel 8").GetAwaiter().GetResult());
        time.Stop();
    }

    private static void Add(ScratchStore store, DemoTime time, string name, string media, byte[]? thumb, int size)
    {
        time.Advance();
        // Real bytes of that size (the demo never opens them), so the card tells a believable size.
        using var content = new MemoryStream(new byte[size]);
        store.AddFileAsync(name, media, content, thumb, "Pixel 8").GetAwaiter().GetResult();
    }

    /// <summary>A picture drawn here (a gradient and a few shapes), as a PNG.</summary>
    private static byte[] Photo(int width, int height, string from, string to)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var context = bitmap.CreateDrawingContext())
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            context.DrawEllipse(new SolidColorBrush(Colors.White, 0.25), null, new Point(width * 0.72, height * 0.3), width * 0.12, width * 0.12);
            context.DrawGeometry(new SolidColorBrush(Colors.Black, 0.25), null, Geometry.Parse(FormattableString.Invariant($"M0,{height} L{width * 0.35},{height * 0.55} L{width * 0.6},{height} Z")));
            context.DrawGeometry(new SolidColorBrush(Colors.Black, 0.35), null, Geometry.Parse(FormattableString.Invariant($"M{width * 0.4},{height} L{width * 0.75},{height * 0.45} L{width},{height} Z")));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A clock that steps back in time while the demo is filled, so the cards carry different times ("3 hours ago"…).</summary>
    private sealed class DemoTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private bool _filling;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Start()
        {
            _filling = true;
            _now = DateTimeOffset.UtcNow.AddHours(-30);
        }

        public void Advance()
        {
            if (_filling) _now = _now.AddMinutes(137);
        }

        public void Stop()
        {
            _filling = false;
            _now = DateTimeOffset.UtcNow;
        }
    }

    private sealed class Host : IScratchPageHost
    {
        public string AddHint => "Press Ctrl+V to add the files, picture or text you copied, or drop files on this page. On your phone, share photos to “Helm Scratch”.";

        public Task PasteAsync() => Task.CompletedTask;
    }

    private sealed class Device : IDeviceInfo
    {
        public string DeviceName => "Work PC";
    }

    private sealed class FakePlatform(string openFolder) : IScratchPlatform
    {
        public string OpenFolder { get; } = openFolder;

        public string SendLabel => "Copy";

        public Task<IReadOnlyList<ScratchSource>> PickFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ScratchSource>>([]);

        public Task<byte[]?> ThumbnailAsync(ScratchSource source, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task OpenAsync(ScratchLocalFile file, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> SaveAsync(ScratchLocalFile file, CancellationToken ct) => Task.FromResult<string?>("Downloads");

        public Task CopyToClipboardAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> SendAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct) => Task.FromResult<string?>("Copied");
    }
}
