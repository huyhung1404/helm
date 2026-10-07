using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Stash;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class StashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _syncKey = SyncKeyring.CreateMasterKey();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public StashTests() => _server.Now = () => _clock.GetUtcNow();

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- Store ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_is_kept_in_the_record_and_long_text_becomes_a_file()
    {
        var store = NewLocal("a").Store;

        var id = await store.AddTextAsync("https://example.com\r\nsecond line", "PC");
        var text = store.Get(id)!;
        Assert.Equal(StashKind.Text, text.Kind);
        Assert.Equal("https://example.com\nsecond line", text.Text);
        Assert.Null(text.Blob);
        Assert.Equal("https://example.com", text.DisplayName);
        Assert.Equal("PC", text.AddedFrom);

        var longText = new string('x', StashItem.MaxTextLength + 1);
        var fileId = await store.AddTextAsync(longText, "PC");
        var file = store.Get(fileId)!;
        Assert.Equal(StashKind.File, file.Kind);
        Assert.EndsWith(".txt", file.Name);
        Assert.Equal("text/plain", file.MediaType);
        using var read = new MemoryStream();
        await store.ReadAsync(file, read);
        Assert.Equal(longText, Encoding.UTF8.GetString(read.ToArray()));

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddTextAsync("  \n ", "PC"));
    }

    [Fact]
    public async Task A_file_is_encrypted_on_disk_and_reads_back()
    {
        var device = NewLocal("a");
        var marker = Encoding.UTF8.GetBytes("HOLIDAY-PHOTO-SECRET-0123456789");

        var id = await device.Store.AddFileAsync("C:\\Users\\me\\Pictures\\beach.JPG", null, new MemoryStream(marker), [1, 2, 3], "Phone");

        var item = device.Store.Get(id)!;
        Assert.Equal("beach.JPG", item.Name);
        Assert.Equal("image/jpeg", item.MediaType);
        Assert.True(item.IsImage);
        Assert.Equal(marker.Length, item.Size);
        Assert.Equal([1, 2, 3], item.Thumbnail);
        Assert.True(device.Store.IsUploading(item));
        foreach (var chunk in Directory.EnumerateFiles(Path.Combine(_dir, "a"), "*.chunk", SearchOption.AllDirectories))
            Assert.Equal(-1, File.ReadAllBytes(chunk).AsSpan().IndexOf(marker));

        using var read = new MemoryStream();
        await device.Store.ReadAsync(item, read);
        Assert.Equal(marker, read.ToArray());
    }

    [Fact]
    public async Task An_oversized_thumbnail_is_dropped_and_the_file_kept()
    {
        var store = NewLocal("a").Store;

        var id = await store.AddFileAsync("clip.mp4", "video/mp4", new MemoryStream([9]), new byte[StashItem.MaxThumbnailBytes + 1], "PC");

        Assert.Null(store.Get(id)!.Thumbnail);
        Assert.True(store.Get(id)!.IsVideo);
    }

    [Fact]
    public async Task Filters_show_the_right_things_newest_first()
    {
        var store = NewLocal("a").Store;
        var photo = await store.AddFileAsync("a.png", "image/png", new MemoryStream([1]), null, "PC");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var pdf = await store.AddFileAsync("b.pdf", null, new MemoryStream([1]), null, "PC");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var text = await store.AddTextAsync("hello", "PC");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var video = await store.AddFileAsync("c.mov", null, new MemoryStream([1]), null, "PC");
        store.MoveToTrash(pdf);

        Assert.Equal([video, text, photo], store.Items(StashFilter.All).Select(i => i.Id));
        Assert.Equal([video, photo], store.Items(StashFilter.Media).Select(i => i.Id));
        Assert.Empty(store.Items(StashFilter.Files));
        Assert.Equal([text], store.Items(StashFilter.Text).Select(i => i.Id));
        Assert.Equal([pdf], store.Items(StashFilter.Trash).Select(i => i.Id));
        var counts = store.Counts();
        Assert.Equal(3, counts[StashFilter.All]);
        Assert.Equal(1, counts[StashFilter.Trash]);
    }

    [Fact]
    public async Task Only_things_in_the_trash_can_be_deleted_and_their_file_goes_at_once()
    {
        var device = NewLocal("a");
        var id = await device.Store.AddFileAsync("doc.pdf", null, new MemoryStream(RandomNumberGenerator.GetBytes(1000)), null, "PC");
        var blob = device.Store.Get(id)!.Blob!;

        Assert.False(device.Store.DeleteForever(id));
        Assert.NotNull(device.Store.Get(id));

        device.Store.MoveToTrash(id);
        Assert.True(device.Store.Get(id)!.Trashed);
        Assert.Equal(_clock.GetUtcNow(), device.Store.Get(id)!.TrashedAt);
        device.Store.Restore(id);
        Assert.False(device.Store.Get(id)!.Trashed);
        Assert.Null(device.Store.Get(id)!.TrashedAt);

        device.Store.MoveToTrash(id);
        Assert.True(device.Store.DeleteForever(id));
        Assert.Null(device.Store.Get(id));
        Assert.False(device.Blobs.IsPending(blob.Id));
        Assert.False(device.Blobs.IsCached(blob));
    }

    [Fact]
    public async Task Emptying_the_trash_deletes_only_what_is_in_it()
    {
        var store = NewLocal("a").Store;
        var keep = await store.AddTextAsync("keep", "PC");
        var a = await store.AddTextAsync("a", "PC");
        var b = await store.AddFileAsync("b.zip", null, new MemoryStream([1]), null, "PC");
        store.MoveToTrash(a);
        store.MoveToTrash(b);

        Assert.Equal(2, store.EmptyTrash());

        Assert.Equal([keep], store.Items(StashFilter.All).Select(i => i.Id));
        Assert.Empty(store.Items(StashFilter.Trash));
    }

    [Fact]
    public async Task Dragging_a_card_moves_it_up_before_or_down_after_the_target()
    {
        var store = NewLocal("a").Store;
        var ids = new List<string>();
        foreach (var text in new[] { "d", "c", "b", "a" })
        {
            ids.Add(await store.AddTextAsync(text, "PC"));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        string Wall() => string.Concat(store.Items(StashFilter.All).Select(i => i.Value.Text));
        string Id(string text) => store.Items(StashFilter.All).Single(i => i.Value.Text == text).Id;
        Assert.Equal("abcd", Wall());

        Assert.True(store.Move(Id("d"), Id("b")));
        Assert.Equal("adbc", Wall());
        Assert.True(store.Move(Id("a"), Id("b")));
        Assert.Equal("dbac", Wall());
        Assert.True(store.Move(Id("c"), Id("d")));
        Assert.Equal("cdba", Wall());
        Assert.True(store.Move(Id("c"), Id("a")));
        Assert.Equal("dbac", Wall());
        Assert.False(store.Move(Id("c"), Id("c")));

        // Something new still goes on top.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.AddTextAsync("n", "PC");
        Assert.Equal("ndbac", Wall());
    }

    [Fact]
    public async Task Renaming_keeps_a_safe_file_name()
    {
        var store = NewLocal("a").Store;
        var id = await store.AddFileAsync("a.txt", null, new MemoryStream([1]), null, "PC");

        store.Rename(id, "..\\secret/<plan>?.txt");

        Assert.Equal("_plan__.txt", store.Get(id)!.Name);
        Assert.Throws<ArgumentException>(() => store.Rename(id, "  "));
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("C:\\Users\\me\\a.png", "a.png")]
    [InlineData("/storage/emulated/0/DCIM/b.mp4", "b.mp4")]
    [InlineData("  ..  ", "file")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("", "file")]
    public void File_names_are_safe_on_both_platforms(string name, string expected) =>
        Assert.Equal(expected, StashFormat.SafeFileName(name));

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var safe = StashFormat.SafeFileName(new string('a', 300) + ".jpeg");
        Assert.Equal(StashItem.MaxNameLength, safe.Length);
        Assert.EndsWith(".jpeg", safe);
    }

    [Theory]
    [InlineData(null, "x.HEIC", "image/heic")]
    [InlineData("application/octet-stream", "x.mp4", "video/mp4")]
    [InlineData("Image/PNG", "x.bin", "image/png")]
    [InlineData("", "x.unknown", "application/octet-stream")]
    public void The_media_type_comes_from_the_platform_or_the_name(string? reported, string name, string expected) =>
        Assert.Equal(expected, StashFormat.PickMediaType(reported, name));

    // ---- Sync ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_added_on_the_phone_opens_on_the_pc()
    {
        var phone = NewSynced("phone");
        var pc = NewSynced("pc");
        var data = RandomNumberGenerator.GetBytes(BlobStore.ChunkSize + 4321);
        var id = await phone.Store.AddFileAsync("IMG_0001.jpg", "image/jpeg", new MemoryStream(data), [7, 7], "Pixel");
        Assert.True(phone.Store.IsUploading(phone.Store.Get(id)!));

        await Sync(phone, pc);

        Assert.False(phone.Store.IsUploading(phone.Store.Get(id)!));
        var item = pc.Store.Get(id)!;
        Assert.Equal("IMG_0001.jpg", item.Name);
        Assert.Equal("Pixel", item.AddedFrom);
        Assert.Equal([7, 7], item.Thumbnail);
        Assert.False(pc.Store.IsOnThisDevice(item));
        using var read = new MemoryStream();
        await pc.Store.ReadAsync(item, read);
        Assert.Equal(data, read.ToArray());
        Assert.True(pc.Store.IsOnThisDevice(item));
    }

    [Fact]
    public async Task The_trash_syncs_and_deleting_from_it_removes_the_file_everywhere()
    {
        var phone = NewSynced("phone");
        var pc = NewSynced("pc");
        var id = await phone.Store.AddFileAsync("scan.pdf", null, new MemoryStream(RandomNumberGenerator.GetBytes(5000)), null, "Pixel");
        var blobId = phone.Store.Get(id)!.Blob!.Id;
        await Sync(phone, pc);

        pc.Store.MoveToTrash(id);
        await Sync(pc, phone);
        Assert.True(phone.Store.Get(id)!.Trashed);

        phone.Store.DeleteForever(id);
        await Sync(phone, pc);
        Assert.Null(pc.Store.Get(id));

        // The server copy goes with the clean-up, once the grace period for uploads in progress has passed.
        _clock.Advance(BlobStore.GarbageGrace + TimeSpan.FromDays(1));
        await Sync(phone);
        Assert.NotNull(_server.Blobs[blobId].DeletedAt);
    }

    [Fact]
    public async Task A_file_deleted_for_good_before_its_upload_is_never_uploaded()
    {
        var phone = NewSynced("phone");
        var id = await phone.Store.AddFileAsync("big.mov", null, new MemoryStream(RandomNumberGenerator.GetBytes(2000)), null, "Pixel");
        var blobId = phone.Store.Get(id)!.Blob!.Id;
        phone.Store.MoveToTrash(id);
        phone.Store.DeleteForever(id);

        await Sync(phone);

        Assert.False(_server.Blobs.ContainsKey(blobId));
    }

    [Fact]
    public async Task Freeing_up_space_keeps_files_that_are_not_uploaded()
    {
        var phone = NewSynced("phone");
        var uploaded = await phone.Store.AddFileAsync("a.jpg", null, new MemoryStream(RandomNumberGenerator.GetBytes(3000)), null, "Pixel");
        await Sync(phone);
        var waiting = await phone.Store.AddFileAsync("b.jpg", null, new MemoryStream(RandomNumberGenerator.GetBytes(3000)), null, "Pixel");

        Assert.True(phone.Store.FreeUpSpace() > 0);

        Assert.False(phone.Store.IsOnThisDevice(phone.Store.Get(uploaded)!));
        Assert.True(phone.Store.IsOnThisDevice(phone.Store.Get(waiting)!));
        using var read = new MemoryStream();
        await phone.Store.ReadAsync(phone.Store.Get(uploaded)!, read);
        Assert.Equal(3000, read.Length);
    }

    // ---- Send to clipboard ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_sent_to_the_clipboard_on_the_phone_is_copied_on_the_pc()
    {
        var phone = NewSynced("phone");
        var pc = NewSynced("pc");
        var phoneRelay = NewRelay(phone, "phone");
        var pcRelay = NewRelay(pc, "pc");
        var id = await phone.Store.AddTextAsync("OTP 482913", "Pixel");

        await phoneRelay.Sender.CopyHereAsync(id, phone.Store.Get(id)!);
        phoneRelay.Sender.Send(id);
        await Sync(phone, pc);

        Assert.Equal("OTP 482913", phoneRelay.Clipboard.Text);
        Assert.Equal("OTP 482913", pcRelay.Clipboard.Text);
        Assert.Equal(["Copied text from Test PC to the clipboard."], pcRelay.Notices);
        // The phone does not copy its own signal again when it comes back.
        await Sync(phone);
        Assert.Empty(phoneRelay.Notices);
    }

    [Fact]
    public async Task A_file_sent_to_the_clipboard_is_downloaded_and_put_on_the_other_clipboard()
    {
        var phone = NewSynced("phone");
        var pc = NewSynced("pc");
        NewRelay(phone, "phone");
        var pcRelay = NewRelay(pc, "pc");
        var data = RandomNumberGenerator.GetBytes(10_000);
        var id = await phone.Store.AddFileAsync("screen.png", null, new MemoryStream(data), null, "Pixel");
        await Sync(phone, pc);

        var phoneRelay = NewRelay(phone, "phone2");
        phoneRelay.Sender.Send(id);
        await Sync(phone, pc);
        // The copy downloads the file first, after the sync that brought the signal.
        for (var i = 0; i < 200 && pcRelay.Notices.Count == 0; i++) await Task.Delay(10);

        Assert.Equal(["Copied “screen.png” from Test PC to the clipboard."], pcRelay.Notices);
        var copied = Assert.Single(pcRelay.Platform.OnClipboard);
        Assert.Equal("screen.png", copied.Name);
        Assert.Equal(data, File.ReadAllBytes(copied.Path));
    }

    [Fact]
    public async Task An_old_signal_or_a_device_that_does_not_receive_never_touches_the_clipboard()
    {
        var phone = NewSynced("phone");
        var pc = NewSynced("pc");
        var tablet = NewSynced("tablet");
        var phoneRelay = NewRelay(phone, "phone");
        var pcRelay = NewRelay(pc, "pc");
        var tabletRelay = NewRelay(tablet, "tablet", receive: false);
        var id = await phone.Store.AddTextAsync("old", "Pixel");
        phoneRelay.Sender.Send(id);
        await Sync(phone);

        // The PC was offline for a while: by the time it syncs, the signal is stale.
        _clock.Advance(StashClipboardRelay.MaxAge + TimeSpan.FromSeconds(1));
        await Sync(pc, tablet);

        Assert.Null(pcRelay.Clipboard.Text);
        Assert.Null(tabletRelay.Clipboard.Text);

        var fresh = await phone.Store.AddTextAsync("fresh", "Pixel");
        phoneRelay.Sender.Send(fresh);
        await Sync(phone, pc, tablet);
        Assert.Equal("fresh", pcRelay.Clipboard.Text);
        Assert.Null(tabletRelay.Clipboard.Text);
    }

    // ---- View model ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_adds_picked_files_with_thumbnails_and_reports_failures()
    {
        var device = NewLocal("a");
        var platform = new FakePlatform(Path.Combine(_dir, "open"));
        var vm = NewViewModel(device.Store, platform, out _);
        platform.Picked =
        [
            new StashSource("cat.png", "image/png", 3, () => new MemoryStream([1, 2, 3])),
            new StashSource("broken.bin", null, null, () => throw new IOException("The file is locked.")),
            new StashSource("notes.pdf", null, 2, () => new MemoryStream([4, 5])),
        ];

        await vm.AddFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(["notes.pdf", "cat.png"], vm.Rows.Select(r => r.Name));
        Assert.Equal([0xFF, 0xD8], device.Store.Get(vm.Rows[1].Id)!.Thumbnail);
        Assert.Null(device.Store.Get(vm.Rows[0].Id)!.Thumbnail);
        Assert.Equal(["cat.png"], platform.ThumbnailsAskedFor);
        Assert.Contains("Added 2 files.", vm.Message);
        Assert.Contains("“broken.bin”: The file is locked.", vm.Message);
        Assert.Equal("Only on this device", vm.Rows[0].Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Opening_a_file_hands_a_decrypted_copy_to_the_platform_and_reuses_it()
    {
        var device = NewLocal("a");
        var platform = new FakePlatform(Path.Combine(_dir, "open"));
        var vm = NewViewModel(device.Store, platform, out _);
        await vm.AddSourcesAsync([new StashSource("report.pdf", null, 4, () => new MemoryStream([1, 2, 3, 4]))]);

        await vm.OpenCommand.ExecuteAsync(vm.Rows[0]);
        await vm.OpenCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(2, platform.Opened.Count);
        Assert.Equal("report.pdf", platform.Opened[0].Name);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(platform.Opened[0].Path));
        Assert.StartsWith(platform.OpenFolder, platform.Opened[0].Path);

        Assert.Equal(4, vm.WipeOpenedCopies());
        Assert.False(File.Exists(platform.Opened[0].Path));
    }

    [Fact]
    public async Task Text_is_copied_and_shown_in_the_details()
    {
        var device = NewLocal("a");
        var platform = new FakePlatform(Path.Combine(_dir, "open"));
        var vm = NewViewModel(device.Store, platform, out var clipboard);
        vm.OpenTextBoxCommand.Execute(null);
        vm.NewText = "Wi-Fi: helm-home / pass 1234";

        await vm.SaveTextCommand.ExecuteAsync(null);

        Assert.False(vm.IsTextBoxOpen);
        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsText);
        await vm.SendCommand.ExecuteAsync(row);
        Assert.Equal("Wi-Fi: helm-home / pass 1234", clipboard.Text);
        Assert.Empty(platform.Sent);

        await vm.OpenCommand.ExecuteAsync(row);
        Assert.Same(row, vm.Selected);
        Assert.Equal("Wi-Fi: helm-home / pass 1234", vm.DetailText);
    }

    [Fact]
    public async Task Trash_restore_and_delete_for_good_follow_the_filter()
    {
        var device = NewLocal("a");
        var vm = NewViewModel(device.Store, new FakePlatform(Path.Combine(_dir, "open")), out _);
        await vm.AddTextAsync("one");
        await vm.AddTextAsync("two");
        var row = vm.Rows.First(r => r.Name == "one");

        vm.TrashCommand.Execute(row);
        Assert.Equal(["two"], vm.Rows.Select(r => r.Name));
        Assert.Equal("Trash (1)", vm.FilterTabs.Single(t => t.Filter == StashFilter.Trash).Label);

        vm.ShowFilterCommand.Execute(vm.FilterTabs.Single(t => t.Filter == StashFilter.Trash));
        Assert.Equal(["one"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].Trashed);

        await vm.DeleteForeverCommand.ExecuteAsync(vm.Rows[0]);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasTrash);
        Assert.Single(device.Store.Items(StashFilter.All));
    }

    [Fact]
    public void The_trash_is_never_the_filter_the_page_opens_on()
    {
        var device = NewLocal("a");
        var settings = Own(new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir, "settings"))));
        var first = NewViewModel(device.Store, new FakePlatform(Path.Combine(_dir, "open")), out _, settings);
        first.ShowFilterCommand.Execute(first.FilterTabs.Single(t => t.Filter == StashFilter.Text));
        first.ShowFilterCommand.Execute(first.FilterTabs.Single(t => t.Filter == StashFilter.Trash));

        var second = NewViewModel(device.Store, new FakePlatform(Path.Combine(_dir, "open")), out _, settings);

        Assert.Equal(StashFilter.Text, second.Filter);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private sealed record Device(StashStore Store, BlobStore Blobs, SyncEngine? Engine, SyncedCollection<StashClipboardSignal>? Signals = null);

    private sealed record Relay(StashClipboardRelay Sender, MemoryClipboard Clipboard, FakePlatform Platform, List<string> Notices);

    private Relay NewRelay(Device device, string name, bool receive = true)
    {
        var settings = Own(new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir, name + "-settings"))));
        settings.Get<StashSettings>(StashIds.ModuleId).Update(s => s.ReceiveClipboard = receive);
        var clipboard = new MemoryClipboard();
        var platform = new FakePlatform(Path.Combine(_dir, name + "-open"));
        var relay = new StashClipboardRelay(device.Signals!, device.Store, platform, clipboard, settings, new FixedDevice(), new InlineUi(), time: _clock);
        var notices = new List<string>();
        relay.Notice += (_, text) => notices.Add(text);
        relay.Start();
        return new Relay(relay, clipboard, platform, notices);
    }

    private Device NewLocal(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory, _clock);
        return new Device(new StashStore(new MemorySynced<StashItem>(), blobs, _clock), blobs, null);
    }

    private Device NewSynced(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var transport = _server.Connect();
        var blobs = new BlobStore(db, transport, paths.SyncBlobsDirectory, _clock);
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_syncKey), [], time: _clock,
            debounce: TimeSpan.FromHours(1), blobs: blobs));
        var items = Own(new SyncedCollection<StashItem>(engine, StashStore.Options));
        var signals = Own(new SyncedCollection<StashClipboardSignal>(engine, StashClipboardRelay.Options));
        return new Device(new StashStore(items, blobs, _clock), blobs, engine, signals);
    }

    private StashViewModel NewViewModel(StashStore store, FakePlatform platform, out MemoryClipboard clipboard, SettingsStoreFactory? settings = null)
    {
        settings ??= Own(new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir, "settings-" + Guid.NewGuid().ToString("N")[..6]))));
        clipboard = new MemoryClipboard();
        var relay = new StashClipboardRelay(new MemorySynced<StashClipboardSignal>(), store, platform, clipboard, settings, new FixedDevice(), new InlineUi());
        return new StashViewModel(store, new StashImporter(store, platform, new FixedDevice()), relay, platform, settings, new InlineUi(),
            new AcceptDialogs(), clipboard, NullLogger<StashViewModel>.Instance);
    }

    private static async Task Sync(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine!.SyncNowAsync()).Outcome);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class FixedDevice : IDeviceInfo
    {
        public string DeviceName => "Test PC";
    }

    private sealed class FakePlatform(string openFolder) : IStashPlatform
    {
        public IReadOnlyList<StashSource> Picked { get; set; } = [];
        public List<string> ThumbnailsAskedFor { get; } = [];
        public List<StashLocalFile> Opened { get; } = [];
        public List<StashLocalFile> Sent { get; } = [];

        public string OpenFolder { get; } = openFolder;
        public string SendLabel => "Copy";

        public Task<IReadOnlyList<StashSource>> PickFilesAsync(CancellationToken ct) => Task.FromResult(Picked);

        public Task<byte[]?> ThumbnailAsync(StashSource source, CancellationToken ct)
        {
            ThumbnailsAskedFor.Add(source.Name);
            return Task.FromResult<byte[]?>([0xFF, 0xD8]);
        }

        public Task OpenAsync(StashLocalFile file, CancellationToken ct)
        {
            Opened.Add(file);
            return Task.CompletedTask;
        }

        public Task<string?> SaveAsync(StashLocalFile file, CancellationToken ct) => Task.FromResult<string?>("Downloads");

        public List<StashLocalFile> OnClipboard { get; } = [];

        public Task CopyToClipboardAsync(StashLocalFile file, CancellationToken ct)
        {
            OnClipboard.Add(file);
            return Task.CompletedTask;
        }

        public Task<string?> SendAsync(IReadOnlyList<StashLocalFile> files, CancellationToken ct)
        {
            Sent.AddRange(files);
            return Task.FromResult<string?>("Copied.");
        }
    }
}
