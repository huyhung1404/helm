using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Missions;
using Helm.Modules.Missions.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Missions a Gallery entry shows.</summary>
internal enum MissionsStage
{
    /// <summary>Three missions, two running (the Today cards), the first on a step with a checklist and resources.</summary>
    Mission,
    /// <summary>The same, on the Insights tab (statistics and badges).</summary>
    Insights,
    /// <summary>A step of the roadmap opened, with its resources, and another one being edited.</summary>
    Roadmap,
    /// <summary>A paused mission.</summary>
    Paused,
    /// <summary>The new mission sheet, step 1: the goal and the prompt.</summary>
    NewMission,
    /// <summary>The new mission sheet, step 2: an AI's answer pasted, with its preview.</summary>
    Import,
    /// <summary>The new plan sheet: what changed.</summary>
    Replan,
    /// <summary>The last step of a phase done: its celebration.</summary>
    PhaseDone,
    /// <summary>The last step of the mission done: the celebration with confetti and the reward.</summary>
    MissionDone,
    /// <summary>No mission yet.</summary>
    FirstRun,
    /// <summary>The settings sheet (the gear).</summary>
    Settings,
}

/// <summary>The real Missions view over an in-memory store: an Aptis mission half way, a run, a reading plan.</summary>
internal static class MissionsDemo
{
    public static Control Create(FakeModule module, MissionsStage stage)
    {
        var store = new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>());
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        settings.Get<MissionsSettings>(MissionsIds.ModuleId).Update(s => s.PlayCelebrations = true);
        string? aptis = null;
        if (stage != MissionsStage.FirstRun) aptis = Seed(store, stage);

        var vm = new MissionsViewModel(store, settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(), new NoLauncher(),
            new MissionReminderService(store, settings), new NoServices(), NullLogger<MissionsViewModel>.Instance);
        if (aptis is not null) vm.Select(aptis);
        var view = new MissionsView(module, vm, new Host());

        void AfterShown(Action action) =>
            view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

        switch (stage)
        {
            case MissionsStage.Insights:
                view.ShowInsights();
                break;
            case MissionsStage.Roadmap:
                var steps = vm.Phases.SelectMany(p => p.Steps).ToList();
                // The current step opened (its resources), and the step after it in its editor.
                var current = steps.First(s => s.IsCurrent);
                vm.ToggleStepCommand.Execute(current);
                current.Resources[0].IsExpanded = true;
                vm.EditStepCommand.Execute(steps.First(s => s.IsUpcoming));
                // Scrolled down to the opened step.
                view.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    if (view.FindControl<ScrollViewer>("Scroller") is not { } scroller
                        || view.GetVisualDescendants().OfType<MissionRoadmap>().FirstOrDefault() is not { } roadmap
                        || roadmap.TranslatePoint(default, (Avalonia.Visual)scroller.Content!) is not { } at) return;
                    scroller.Offset = new Avalonia.Vector(0, at.Y + 300);
                }, TimeSpan.FromMilliseconds(300));
                break;
            case MissionsStage.NewMission:
                vm.OpenPromptCommand.Execute(null);
                vm.PromptGoal = "Đạt Aptis B1 trước kỳ thi ngày 15/12";
                vm.PromptLevel = "TOEIC khoảng 300, nghe yếu";
                vm.PromptTime = "1 tiếng mỗi tối, 3 tiếng cuối tuần";
                vm.PromptNotes = "Thích học qua video và flashcard";
                break;
            case MissionsStage.Import:
                vm.OpenImportCommand.Execute(null);
                vm.ImportText = "Đây là lộ trình cho bạn:\n\n```json\n" + PlanJson + "\n```\n\nChúc bạn học tốt!";
                break;
            case MissionsStage.Replan:
                vm.OpenReplanCommand.Execute(null);
                vm.ReplanReason = "Tuần này đi công tác, chỉ học được 30 phút mỗi tối";
                break;
            case MissionsStage.PhaseDone:
            case MissionsStage.MissionDone:
                AfterShown(() => vm.CompleteStepCommand.Execute(null));
                break;
            case MissionsStage.Settings:
                AfterShown(() => view.OpenSettings());
                break;
        }
        return view;
    }

    /// <summary>The missions; returns the one to show.</summary>
    private static string Seed(MissionsStore store, MissionsStage stage)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var vocabulary = new MissionResource
        {
            Label = "Từ vựng",
            Title = "20 từ chủ đề Du lịch",
            Text = "Học theo cụm, đọc to mỗi từ ba lần rồi đặt một câu.",
            Columns = ["Word", "Nghĩa", "Ví dụ"],
            Rows =
            [
                ["itinerary", "lịch trình", "Our itinerary includes two days in Hội An."],
                ["accommodation", "chỗ ở", "The accommodation was clean and cheap."],
                ["departure", "khởi hành", "Departure is at 6 a.m."],
            ],
        };
        var practice = new MissionResource { Label = "Luyện đề", Title = "British Council – Aptis practice tests", Url = "https://www.britishcouncil.org/exam/aptis" };

        var plan = new MissionDraft("Thi Aptis B1", "Đạt B1 cả bốn kỹ năng ở kỳ thi ngày 15/12", "Tối thiểu 30 phút mỗi ngày, kể cả cuối tuần.",
            "Một chuyến Đà Lạt cuối tuần", today.AddDays(19),
        [
            new PhaseDraft("Nền tảng", "Một ly trà sữa", [
                new StepDraft("Làm bài test xếp trình độ", "Làm đề thử trên trang của British Council, ghi điểm từng phần.", "Có điểm của cả bốn phần", 1),
                new StepDraft("Ôn 500 từ thông dụng", "Mỗi ngày 50 từ bằng flashcard.", "Thuộc 500 từ, sai dưới 10%", 5),
                new StepDraft("Ngữ pháp: thì hiện tại và quá khứ", "", "Làm đúng 80% bài tập", 3),
            ]),
            new PhaseDraft("Kỹ năng", "Bộ tai nghe mới", [
                new StepDraft("Nghe: 10 bài hội thoại ngắn", "Nghe hai lần, lần hai có transcript.", "Trả lời đúng 7/10 câu", 4),
                new StepDraft("Đọc: chủ đề Du lịch", "Đọc ba bài ngắn, gạch chân từ mới, học 20 từ chủ đề.", "Tóm tắt mỗi bài bằng 3 câu", 3,
                    ["Bài 1: Một ngày ở Hội An", "Bài 2: Đặt phòng khách sạn", "Bài 3: Lịch trình chuyến đi", "Học 20 từ mới"], [vocabulary, practice]),
                new StepDraft("Viết: email 120 từ", "Viết thư cho bạn kể về chuyến đi.", "Được góp ý và sửa xong", 3),
                new StepDraft("Nói: mô tả một bức ảnh", "Ghi âm 1 phút, nghe lại và sửa.", "Nói đủ 1 phút không ngắt quá 3 giây", 3),
            ]),
            new PhaseDraft("Luyện đề", "", [
                new StepDraft("Đề thi thử số 1", "Làm trong đúng thời gian.", "Đạt B1 ở ít nhất ba phần", 2, null, [practice]),
                new StepDraft("Sửa lỗi sai của đề 1", "", "", 2),
                new StepDraft("Đề thi thử số 2", "", "Đạt B1 cả bốn phần", 2),
            ]),
        ]);

        // The phase or the whole mission ends with the step on screen.
        if (stage == MissionsStage.MissionDone) plan = plan with { Phases = [plan.Phases[0], plan.Phases[1] with { Steps = plan.Phases[1].Steps.Take(2).ToList() }] };

        var id = store.Create(plan, MissionSource.Import);
        store.Start(id);
        store.Complete(id, "Listening 3.5, Reading 4, Writing 3, Speaking 3");
        store.Complete(id, "Anki, 512 từ");
        store.Complete(id);
        store.Complete(id, "7/10, bài 6 khó");
        if (stage is MissionsStage.PhaseDone)
        {
            // The last step of "Kỹ năng" is the one on screen.
            store.Complete(id);
            store.Complete(id);
        }
        store.StartStep(id);
        store.ToggleChecklist(id, 0);
        store.ToggleChecklist(id, 1);
        if (stage == MissionsStage.MissionDone)
        {
            // All ticked: it completes without asking.
            store.ToggleChecklist(id, 2);
            store.ToggleChecklist(id, 3);
        }
        if (stage == MissionsStage.Paused) store.Pause(id);

        var run = store.Create(new MissionDraft("Chạy 10 km", "Chạy 10 km không nghỉ trước Tết", "", "Đôi giày chạy mới", today.AddDays(60),
        [
            new PhaseDraft("Làm quen", "", [
                new StepDraft("Chạy bộ xen đi bộ 20 phút, 3 buổi", "", "Đủ 3 buổi trong tuần", 7, ["Buổi 1", "Buổi 2", "Buổi 3"]),
                new StepDraft("Chạy liền 2 km", "", "2 km không đi bộ", 4),
            ]),
            new PhaseDraft("Tăng dần", "", [
                new StepDraft("Chạy 5 km", "", "", 7),
                new StepDraft("Chạy 8 km", "", "", 10),
                new StepDraft("Chạy 10 km", "", "", 10),
            ]),
        ]), MissionSource.Import);
        store.Start(run);
        store.Complete(run);

        store.Create(new MissionDraft("Đọc 12 cuốn sách", "Mỗi tháng một cuốn, ghi chú ngắn", "", "", null,
        [
            new PhaseDraft("Quý 1", "", [new StepDraft("Nhà giả kim", "", "", 30), new StepDraft("Đắc nhân tâm", "", "", 30), new StepDraft("Sapiens", "", "", 30)]),
        ]), MissionSource.Import);
        return id;
    }

    /// <summary>An AI's answer for the Import stage (in the import format).</summary>
    private const string PlanJson = """
        {
          "helmMission": 1,
          "title": "Học 300 chữ Hán HSK3",
          "goal": "Đọc và viết được 300 chữ của HSK3",
          "reward": "Một bộ sách truyện tranh tiếng Trung",
          "phases": [
            { "title": "Bộ thủ", "reward": "Một buổi xem phim", "steps": [
              { "title": "Học 50 bộ thủ thường gặp", "doneWhen": "Nhận ra 45/50", "estimateDays": 5 },
              { "title": "Viết mỗi bộ thủ 5 lần", "estimateDays": 3 }
            ] },
            { "title": "Chữ HSK3", "steps": [
              { "title": "100 chữ đầu", "description": "20 chữ mỗi ngày, ôn bằng flashcard.", "estimateDays": 5 },
              { "title": "100 chữ tiếp theo", "estimateDays": 5 },
              { "title": "100 chữ cuối", "estimateDays": 5 },
              { "title": "Làm đề đọc HSK3", "doneWhen": "Đúng 70%", "estimateDays": 2 }
            ] }
          ]
        }
        """;

    private sealed class Host : IMissionsPageHost
    {
        public void Opened() { }

        public void ExportCsv(string csv) { }
    }

    private sealed class NoLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }

    /// <summary>No Notes, no Tracker: Missions on its own.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
