using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Helm.BugReports.Ui;
using Helm.Core.Diagnostics;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Ui;
using Helm.Shell.BugReports;

namespace Helm.Gallery;

/// <summary>Which Bug reports a Gallery entry shows.</summary>
internal enum BugReportsStage
{
    /// <summary>Six bugs of every kind, none opened (a wide screen says to pick one).</summary>
    List,
    /// <summary>An agent took a Vault bug and asks a question.</summary>
    AgentAsks,
    /// <summary>A Wallet fix waiting for the user to check it.</summary>
    FixToCheck,
    /// <summary>A crash reported from its black box (errors, a freeze, a stack).</summary>
    Crash,
    /// <summary>A closed bug.</summary>
    Closed,
    /// <summary>The crash's black box in its sheet.</summary>
    BlackBox,
    /// <summary>"Close #B6?" over the newest bug.</summary>
    CloseSheet,
    /// <summary>"Still broken?" over the fix to check.</summary>
    ReopenSheet,
    /// <summary>A picture of the newest bug, big.</summary>
    Picture,
    /// <summary>The phone's settings sheet (Shake to report).</summary>
    Settings,
    /// <summary>No bug reported yet.</summary>
    Empty,
    /// <summary>The report form as the PC's window shows it (keyboard, a picture added).</summary>
    Form,
    /// <summary>The report form as the phone shows it (gallery, camera, paste).</summary>
    FormPhone,
    /// <summary>The form opened from a crash's black box.</summary>
    FormCrash,
    /// <summary>The form after Send.</summary>
    FormSent,
}

/// <summary>
/// The real Bug reports views over in-memory stores: bugs reported on the PC and the phone, worked on by an agent, a fix
/// to check, a crash with its black box, closed ones.
/// </summary>
internal static class BugReportsDemo
{
    private static readonly string[] Tools = ["Habits", "Notes", "Scratch", "SSH", "Tracker", "Vault", "Vocabulary", "Wallet"];

    public static Control Create(BugReportsStage stage)
    {
        var (store, clock) = SeededStore(seed: stage is not (BugReportsStage.Empty or BugReportsStage.Form or BugReportsStage.FormPhone
            or BugReportsStage.FormCrash or BugReportsStage.FormSent));
        var time = (DemoClock)clock;
        var box = new BlackBox();

        switch (stage)
        {
            case BugReportsStage.Form or BugReportsStage.FormPhone or BugReportsStage.FormCrash or BugReportsStage.FormSent:
                return Form(store, box, time, stage);
        }

        var vm = new BugReportsViewModel(store, box, new NoClipboard(), null, time);
        var settings = stage == BugReportsStage.Settings
            ? new SettingsStoreFactory(new HelmPaths(Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N")))).Get<BugReportSettings>(BugReportSettings.StoreId)
            : null;
        var view = new BugReportsView(vm, new PageHost(settings));
        string? Find(string tool) => store.All().FirstOrDefault(b => b.Value.Tool == tool)?.Id;

        void Later(Action action) => view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        switch (stage)
        {
            case BugReportsStage.AgentAsks:
                vm.Open(Find("Vault")!);
                break;
            case BugReportsStage.FixToCheck:
                vm.Open(Find("Wallet")!);
                break;
            case BugReportsStage.Crash:
                vm.Open(Find("Notes")!);
                break;
            case BugReportsStage.Closed:
                vm.Filter = BugFilter.Closed;
                vm.Open(Find("Habits")!);
                break;
            case BugReportsStage.BlackBox:
                vm.Open(Find("Notes")!);
                Later(() => view.Detail.ShowBlackBox());
                break;
            case BugReportsStage.CloseSheet:
                vm.Open(Find("Tracker")!);
                Later(() => view.Detail.AskClose());
                break;
            case BugReportsStage.ReopenSheet:
                vm.Open(Find("Wallet")!);
                Later(() => view.Detail.AskReopen(stillBroken: true));
                break;
            case BugReportsStage.Picture:
                vm.Open(Find("Tracker")!);
                Later(() =>
                {
                    if (vm.Selected?.Entries.SelectMany(e => e.Images).FirstOrDefault() is { } image) view.Detail.ShowPicture(image);
                });
                break;
            case BugReportsStage.Settings:
                Later(() => view.OpenSettings());
                break;
        }
        return view;
    }

    /// <summary>The demo's bugs in a store of their own (for tests): returns the store and the clock it runs on.</summary>
    internal static (BugStore Store, TimeProvider Time) SeededStore(bool seed = true)
    {
        var time = new DemoClock();
        var store = new BugStore(new MemorySynced<BugReport>(), new MemorySynced<BugEntry>(), new MemoryBugFiles(), () => time.Device,
            BugDeviceKind.Pc, "0.92.1", time);
        if (seed) Seed(store, time);
        time.Device = "Home PC";
        time.Now = DateTimeOffset.UtcNow;
        return (store, time);
    }

    /// <summary>A black box like the one the report form froze (a Tracker reminder shown twice).</summary>
    internal static BlackBoxSnapshot SampleBox() => TrackerBox(DateTimeOffset.UtcNow);

    internal static IReadOnlyList<string> ToolNames => Tools;

    // ---- the report form ----

    private static Control Form(BugStore store, BlackBox box, DemoClock time, BugReportsStage stage)
    {
        var crash = stage == BugReportsStage.FormCrash;
        var snapshot = crash ? CrashBox(time.Now.AddDays(-1).AddHours(-3)) : TrackerBox(time.Now);
        var form = new BugReportFormViewModel(store, box, snapshot, crash ? null : "Tracker", Tools, fromCrash: crash);
        var phone = stage == BugReportsStage.FormPhone;
        if (stage is BugReportsStage.Form or BugReportsStage.FormSent)
        {
            form.Text = "Kéo thả việc giữa hai danh sách thì việc biến mất\nKéo \"Gọi điện cho mẹ\" từ Hôm nay sang Nhà: nó không hiện ở danh sách nào, sau khi đồng bộ thì quay lại Hôm nay.";
            form.Severity = BugSeverity.Blocking;
            form.AddImage(new BugImageDraft(Screenshot("Tracker", ["Hôm nay · 6 việc", "Nhà · 1 việc", "Gọi điện cho mẹ ?"]), "image/png", "Tracker drag.png"));
        }
        if (stage == BugReportsStage.FormSent) form.SendCommand.Execute(null);
        var view = new BugReportFormView(form, new FormHost(phone));
        // The PC shows the form in a small window: here a card of that size over the page.
        if (phone) return view;
        // The PC window's sheets open over the window, not the whole screen: the card is their "screen".
        return new Panel
        {
            Children =
            {
                new Backdrop(),
                new Border
                {
                    MaxWidth = 680,
                    Margin = new Thickness(24),
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(16),
                    ClipToBounds = true,
                    Classes = { "screen" },
                    Child = view,
                },
            },
        };
    }

    // ---- the bugs ----

    private static void Seed(BugStore store, DemoClock time)
    {
        var now = DateTimeOffset.UtcNow;

        // Closed: not a bug (Scratch works as designed).
        time.At(now.AddDays(-12), "Pixel 8");
        var scratch = Report(store, "Scratch xoá mất mấy tấm ảnh mình để đó\nẢnh chụp màn hình để trong Scratch từ tuần trước tự biến mất.", BugSeverity.Annoying, "Scratch",
            images: [], box: null);
        time.At(now.AddDays(-11.8), "Home PC");
        store.Close(scratch, BugCloseReason.NotABug, "Đúng như thiết kế: thứ không dùng 7 ngày vào thùng rác 30 ngày");

        // Closed: fixed, checked by the user.
        time.At(now.AddDays(-9), "Home PC");
        var habits = Report(store, "Habits hỏi lại lý do bỏ ngày dù mình đã trả lời rồi\nTrả lời lý do cho 2 ngày bỏ tập, mở lại trang thì nó lại hỏi đúng hai ngày đó.", BugSeverity.Annoying, "Habits",
            images: [], box: HabitsBox(time.Now));
        time.At(now.AddDays(-8.9), "helm");
        store.Claim(habits, "Claude Code · helm", "fix/habits-lapse-twice");
        time.At(now.AddDays(-8.7), "helm");
        store.Note(habits, "Claude Code · helm", "Tìm ra rồi: lý do được lưu theo ngày của PC, còn câu hỏi tính theo ngày của điện thoại (múi giờ khác lúc đi công tác).");
        time.At(now.AddDays(-8.5), "helm");
        store.Resolve(habits, "Claude Code · helm", "Nguyên nhân: lý do lưu theo ngày giờ máy, câu hỏi tính theo ngày đồng bộ. Đã đổi cả hai sang ngày của thói quen.", "0.91.0", "fix/habits-lapse-twice");
        time.At(now.AddDays(-6), "Home PC");
        store.ConfirmFixed(habits);

        // To verify: a Wallet fix, after a question that was answered.
        time.At(now.AddDays(-4), "Pixel 8");
        var wallet = Report(store, "Chữ số dư trong vòng tròn bị cắt trên điện thoại\nSố dư 12.450.000 đ bị cắt mất hai chữ số cuối trong vòng tròn ở widget và trang Wallet.", BugSeverity.Minor, "Wallet",
            images: [new BugImageDraft(Screenshot("Wallet", ["Số dư", "12.450.0", "Tháng 11"]), "image/png", "Wallet ring.png")], box: WalletBox(time.Now));
        time.At(now.AddDays(-3.9), "helm");
        store.Claim(wallet, "Claude Code · helm", "fix/wallet-ring-fit");
        time.At(now.AddDays(-3.85), "helm");
        var q = store.Ask(wallet, "Claude Code · helm", "Máy của Anh đang để cỡ chữ hệ thống là bao nhiêu (Cài đặt → Hiển thị → Cỡ chữ)?");
        time.At(now.AddDays(-3.6), "Pixel 8");
        store.AnswerAsync(wallet, "Cỡ chữ lớn nhất, và thu phóng màn hình cũng lớn.", [], null).GetAwaiter().GetResult();
        time.At(now.AddDays(-3.2), "helm");
        store.Resolve(wallet, "Claude Code · helm", "Chữ trong vòng được đo theo đường kính và thu nhỏ khi cỡ chữ hệ thống lớn. Thử với cỡ chữ lớn nhất trên LDPlayer.", "0.92.1", "fix/wallet-ring-fit");
        _ = q;

        // A crash, reported the next morning from its black box.
        time.At(now.AddDays(-1), "Home PC");
        Report(store, "Helm tự tắt khi đang đồng bộ Notes\nĐang sửa một ghi chú dài thì cửa sổ đứng khoảng 2 giây rồi Helm tắt hẳn.", BugSeverity.Blocking, "Notes",
            images: [], box: CrashBox(time.Now.AddHours(-14)), fromCrash: true);

        // Fixing: an agent took a Vault bug and asks.
        time.At(now.AddHours(-5), "Pixel 8");
        var vault = Report(store, "Tìm trong Vault không ra gì sau khi mở khoá bằng vân tay\nMở khoá bằng vân tay rồi gõ \"vietcombank\" thì danh sách trống, dù mục đó vẫn còn.", BugSeverity.Blocking, "Vault",
            images: [new BugImageDraft(Screenshot("Vault", ["Tìm: vietcombank", "Không có kết quả", ""]), "image/png", "Vault search.png")], box: VaultBox(time.Now));
        time.At(now.AddHours(-4.5), "helm");
        store.Claim(vault, "Claude Code · helm", "fix/vault-search-unlock");
        time.At(now.AddHours(-4.2), "helm");
        store.Note(vault, "Claude Code · helm", "Tái hiện được trên LDPlayer: sau khi mở khoá bằng sinh trắc học, chỉ mục tìm kiếm chưa được dựng lại.");
        time.At(now.AddHours(-4), "helm");
        store.Ask(vault, "Claude Code · helm", "Khi mở khoá bằng mật khẩu chính thay vì vân tay, tìm kiếm có chạy đúng không? Nếu đúng thì lỗi chỉ ở đường mở khoá sinh trắc học.");

        // Open: the newest, from the PC, with a picture.
        time.At(now.AddMinutes(-12), "Home PC");
        Report(store, "Nhắc việc \"Trả tiền điện\" hiện hai lần cùng lúc\nSáng nay 9:00 thông báo hiện 2 lần, bấm Xong ở cái thứ nhất thì cái thứ hai vẫn còn.", BugSeverity.Annoying, "Tracker",
            images: [new BugImageDraft(Screenshot("Tracker", ["Trả tiền điện tháng 11", "Trả tiền điện tháng 11", "09:00"]), "image/png", "Two reminders.png")], box: TrackerBox(time.Now));
    }

    private static string Report(BugStore store, string text, BugSeverity severity, string tool, IReadOnlyList<BugImageDraft> images, BlackBoxSnapshot? box, bool fromCrash = false) =>
        store.ReportAsync(new BugDraft(text, severity, tool, images, box, fromCrash)).GetAwaiter().GetResult();

    // ---- black boxes ----

    private static BlackBoxSnapshot Box(DateTimeOffset frozen, string page, params (double At, BlackBoxKind Kind, string Text, string? Source, string? Detail)[] events) =>
        new(frozen, [.. events.Select(e => new BlackBoxEvent(frozen.AddSeconds(e.At), e.Kind, e.Text, e.Source, e.Detail))],
            new Dictionary<string, string>
            {
                ["Helm"] = "0.92.1",
                ["Device"] = "Home PC · Microsoft Windows NT 10.0.26200.0 · 64-bit",
                ["Runtime"] = ".NET 8.0.22",
                ["Page"] = page,
                ["Sync"] = "Idle · last " + frozen.ToLocalTime().AddSeconds(-41).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["Memory"] = "412 MB working set, 96 MB managed",
                ["Culture"] = "vi-VN",
            });

    private static BlackBoxSnapshot TrackerBox(DateTimeOffset at) => Box(at, "Tracker",
        (-58.2, BlackBoxKind.Sync, "Synced", "Sync", null),
        (-44.0, BlackBoxKind.Log, "[INF] Reminder due: Trả tiền điện tháng 11", "TrackerReminderService", null),
        (-43.9, BlackBoxKind.Log, "[INF] Reminder due: Trả tiền điện tháng 11", "TrackerReminderService", null),
        (-40.3, BlackBoxKind.Input, "Clicked the notification", "Tray", null),
        (-40.1, BlackBoxKind.Navigation, "Opened Tracker", "Shell", null),
        (-31.7, BlackBoxKind.Input, "Clicked Done", "TrackerView", null),
        (-12.4, BlackBoxKind.Input, "Pressed Ctrl+Alt+F12", "Hotkeys", null));

    private static BlackBoxSnapshot HabitsBox(DateTimeOffset at) => Box(at, "Habits",
        (-50.0, BlackBoxKind.Navigation, "Opened Habits", "Shell", null),
        (-48.1, BlackBoxKind.Navigation, "Sheet: reason for skipped days", "HabitsView", null),
        (-36.0, BlackBoxKind.Input, "Typed 42 characters", "ReasonBox", null),
        (-30.2, BlackBoxKind.Input, "Clicked Save the reason", "HabitsPopupView", null),
        (-22.5, BlackBoxKind.Navigation, "Opened Home", "Shell", null),
        (-15.0, BlackBoxKind.Navigation, "Opened Habits", "Shell", null),
        (-14.9, BlackBoxKind.Navigation, "Sheet: reason for skipped days", "HabitsView", null));

    private static BlackBoxSnapshot WalletBox(DateTimeOffset at) => Box(at, "Wallet",
        (-25.0, BlackBoxKind.Navigation, "Opened Wallet", "Drawer", null),
        (-18.0, BlackBoxKind.Input, "Tapped Month", "WalletView", null),
        (-6.0, BlackBoxKind.Input, "Shook the phone", "Shake", null));

    private static BlackBoxSnapshot VaultBox(DateTimeOffset at) => Box(at, "Vault",
        (-52.0, BlackBoxKind.Navigation, "Opened Vault", "Drawer", null),
        (-49.5, BlackBoxKind.Input, "Unlocked with the fingerprint", "VaultView", null),
        (-47.0, BlackBoxKind.Input, "Tapped the search box", "VaultView", null),
        (-41.2, BlackBoxKind.Input, "Typed 11 characters", "Search", null),
        (-40.8, BlackBoxKind.Log, "[INF] Search: 0 of 214 items", "VaultViewModel", null),
        (-20.0, BlackBoxKind.DisabledClick, "Tapped Copy (disabled)", "VaultView", null),
        (-4.0, BlackBoxKind.Input, "Tapped the bug in the app bar", "AppBar", null));

    private static BlackBoxSnapshot CrashBox(DateTimeOffset at) => Box(at, "Notes",
        (-59.0, BlackBoxKind.Navigation, "Opened Notes", "Shell", null),
        (-55.2, BlackBoxKind.Input, "Clicked \"Kế hoạch Aptis tuần 3\"", "NotesView", null),
        (-41.0, BlackBoxKind.Input, "Typed 380 characters", "NoteEditor", null),
        (-30.4, BlackBoxKind.Sync, "Sync: Syncing (pushing 3 changes)", "Sync", null),
        (-12.6, BlackBoxKind.Performance, "The UI did not answer for 1.8 s", "UI thread ms=1840", null),
        (-10.7, BlackBoxKind.Error, "[ERR] Sync push failed: ObjectDisposedException: Cannot access a closed file.", "SyncEngine",
            "System.ObjectDisposedException: Cannot access a closed file.\n   at System.IO.FileStream.Write(ReadOnlySpan`1 buffer)\n   at Helm.Core.Sync.BlobStore.ImportAsync(Stream source, String name, CancellationToken ct)\n   at Helm.Core.Sync.SyncEngine.PushAsync(CancellationToken ct)"),
        (-10.6, BlackBoxKind.Error, "[FTL] Unhandled exception: InvalidOperationException: Collection was modified", "App",
            "System.InvalidOperationException: Collection was modified; enumeration operation may not execute.\n   at Helm.Modules.Notes.NotesStore.Save(String id, Note note)\n   at Helm.Modules.Notes.Ui.NoteEditor.OnTextChanged()"),
        (-0.1, BlackBoxKind.Log, "[INF] Helm is closing", "App", null));

    // ---- pictures ----

    /// <summary>A small made-up screenshot: a dark Helm card with three lines.</summary>
    private static byte[] Screenshot(string tool, string[] lines)
    {
        const int width = 640, height = 400;
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var context = bitmap.CreateDrawingContext())
        {
            var back = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#15171C"), 0), new GradientStop(Color.Parse("#0B0C0F"), 1) },
            };
            context.DrawRectangle(back, null, new Rect(0, 0, width, height));
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#0077F2"), 0.25), null, new Point(60, 40), 160, 120);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#00CFC0"), 0.18), null, new Point(600, 380), 180, 120);
            var face = new Typeface("Segoe UI");
            void Text(string text, double x, double y, double size, IBrush brush) =>
                context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush), new Point(x, y));
            Text(tool.ToUpperInvariant(), 40, 34, 15, new SolidColorBrush(Color.Parse("#2FD3C1")));
            for (var i = 0; i < lines.Length; i++)
            {
                var top = 80 + i * 96;
                context.DrawRectangle(new SolidColorBrush(Colors.White, 0.08), new Pen(new SolidColorBrush(Colors.White, 0.13)), new Rect(40, top, 560, 80), 12, 12);
                Text(lines[i], 64, top + 24, 24, Brushes.White);
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    // ---- stand-ins ----

    /// <summary>Bug pictures and black boxes in memory (the real ones are sync blobs).</summary>
    private sealed class MemoryBugFiles : IBugFiles
    {
        private readonly Dictionary<string, byte[]> _blobs = [];

        public Task<BlobRef> SaveAsync(byte[] data, CancellationToken ct = default)
        {
            var id = Guid.NewGuid().ToString("N");
            _blobs[id] = data;
            return Task.FromResult(new BlobRef(id, data.Length, data.Length, 1, []));
        }

        public Task<byte[]> ReadAsync(BlobRef blob, CancellationToken ct = default) => Task.FromResult(_blobs[blob.Id]);
    }

    /// <summary>A clock the seeding moves through the last days, and the device each step happens on.</summary>
    private sealed class DemoClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public string Device { get; set; } = "Home PC";

        public void At(DateTimeOffset at, string device)
        {
            Now = at;
            Device = device;
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PageHost(ISettingsStore<BugReportSettings>? shake) : IBugReportsPageHost
    {
        public void Report() { }

        public string ReportHint => "Ctrl+Alt+F12, anywhere";

        public ISettingsStore<BugReportSettings>? ShakeSettings => shake;

        public Task<IReadOnlyList<BugImageDraft>> PickAsync(int max) =>
            Task.FromResult<IReadOnlyList<BugImageDraft>>([new BugImageDraft(Screenshot("Picked", ["A picture you chose", "", ""]), "image/png", "Picked.png")]);

        public Task<IReadOnlyList<BugImageDraft>> PasteAsync() => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);
    }

    private sealed class FormHost(bool phone) : IBugReportFormHost
    {
        public void Close() { }

        public void OpenBug(string id) { }

        public bool HasKeyboard => !phone;

        public bool CanTakePhoto => phone;

        public Task<IReadOnlyList<BugImageDraft>> PickAsync(int max) =>
            Task.FromResult<IReadOnlyList<BugImageDraft>>([new BugImageDraft(Screenshot("Picked", ["A picture you chose", "", ""]), "image/png", "Picked.png")]);

        public Task<IReadOnlyList<BugImageDraft>> PasteAsync() => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);
    }
}
