using Avalonia.Controls;
using Helm.Core.Settings;
using Helm.Core.Tasks;
using Helm.Modules.Notes;
using Helm.Modules.Notes.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Notes a Gallery entry shows.</summary>
internal enum NotesStage
{
    /// <summary>A wall of notes, some pinned, a few in the trash.</summary>
    Wall,
    /// <summary>The same wall with one note open.</summary>
    Open,
    /// <summary>No note yet.</summary>
    Empty,
}

/// <summary>The real Notes view over an in-memory store, with a "Make tasks" that answers at once.</summary>
internal static class NotesDemo
{
    public static Control Create(FakeModule module, NotesStage stage)
    {
        var store = new NotesStore(new MemorySynced<NoteItem>());
        string? open = null;
        if (stage != NotesStage.Empty)
        {
            store.Add("Ý tưởng cho Helm", "- Home là \"hôm nay\": việc cần làm, thói quen, hoá đơn\n- Scratch: kéo thả ảnh từ điện thoại sang PC\n- Notes: tìm theo nghĩa, không chỉ theo chữ\n- Thử Avalonia trên macOS", pinned: true);
            open = store.Add("Họp sprint thứ Hai", "Chốt:\n1. Gửi báo cáo tuần trước 17h thứ Sáu\n2. Anh Minh review PR đăng nhập trước thứ Tư\n3. Hẹn khách hàng demo ngày 3/12\n\nCần hỏi lại: ngân sách quảng cáo tháng 12.");
            store.Add("Wifi nhà bà ngoại", "Tên: TP-LINK_8A2C\nMật khẩu: hỏi cậu Long", pinned: true);
            store.Add("Danh sách mua sắm", "Sữa tươi\nTrứng\nCà phê Trung Nguyên\nGiấy ăn\nBột giặt");
            store.Add("Nginx 502 khi deploy", "upstream timed out → tăng proxy_read_timeout lên 120s\nkiểm tra lại health check của container\n\nlocation /api {\n    proxy_pass http://app:8080;\n    proxy_read_timeout 120s;\n}");
            store.Add("Sách muốn đọc", "Atomic Habits\nDeep Work\nNhà giả kim\nĐắc nhân tâm (đọc lại)");
            store.Add("Quà sinh nhật mẹ", "Khăn lụa màu xanh, hoặc đặt bàn ở nhà hàng chay gần nhà.");
            store.Add("", "Một ghi chú không có tiêu đề: dòng đầu tiên thành tên của nó.");
            store.Add("Lịch tập gym", "Thứ 2 - ngực, vai\nThứ 4 - lưng, tay\nThứ 6 - chân\nChủ nhật - chạy 5km");
            store.Add("Câu Aptis hay gặp", "Describe a place you like to visit at weekends.\nWhat do you usually do in your free time?\nTalk about a person who influenced you.");
            store.MoveToTrash(store.Add("Nháp email cũ", "Gửi anh Tuấn bảng giá tháng 10."));
            store.MoveToTrash(store.Add("Số điện thoại thợ sửa điều hoà", "0903 xxx xxx"));
        }

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        var vm = new NotesViewModel(store, settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(), NullLogger<NotesViewModel>.Instance,
            taskMaker: new FakeTaskMaker());
        if (open is not null && stage == NotesStage.Open) vm.OpenNote(open);
        return new NotesView(module, vm, new Host());
    }

    private sealed class Host : INotesPageHost
    {
        public bool CanExport => true;
    }

    /// <summary>"Make tasks" with a ready answer: three tasks from the meeting note.</summary>
    private sealed class FakeTaskMaker : INoteTaskMaker
    {
        public string ModuleId => "tracker";
        public string Name => "Tracker";
        public bool IsAvailable => true;

        public event EventHandler? TasksChanged { add { } remove { } }

        public string? Where() => "Haiku 5.5 · demo";

        public IReadOnlyList<string> Lists() => ["Hôm nay", "Helm", "Nhà"];

        public async Task<NoteTaskAnswer> ProposeAsync(string title, string text, CancellationToken ct)
        {
            await Task.Delay(1200, ct).ConfigureAwait(true);
            var friday = DateTime.Today.AddDays(((int)DayOfWeek.Friday - (int)DateTime.Today.DayOfWeek + 7) % 7);
            return new NoteTaskAnswer(
            [
                new("Gửi báo cáo tuần", "", friday.ToString("yyyy-MM-dd") + "T17:00", friday.ToString("ddd d MMM"), "1. Gửi báo cáo tuần trước 17h thứ Sáu"),
                new("Nhắc anh Minh review PR đăng nhập", "", null, "", "2. Anh Minh review PR đăng nhập trước thứ Tư"),
                new("Hỏi lại ngân sách quảng cáo tháng 12", "", null, "", "Cần hỏi lại: ngân sách quảng cáo tháng 12."),
            ], null);
        }

        public NoteTaskBatch Add(string listName, IReadOnlyList<NoteTaskProposal> tasks) =>
            new(listName, [.. tasks.Select((_, i) => "demo-" + i)], null);

        public void Undo(NoteTaskBatch batch) { }

        public bool? IsDone(string taskId) => false;
    }
}
