using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Modules.Habits;
using Helm.Modules.Habits.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Habits a Gallery entry shows.</summary>
internal enum HabitsStage
{
    /// <summary>Five habits with months of history, two done today.</summary>
    Today,
    /// <summary>The same, on the Report tab.</summary>
    Report,
    /// <summary>The sheet marking today of a habit (how much of it was done).</summary>
    MarkDay,
    /// <summary>The celebration after a habit is done for the day (with confetti).</summary>
    Celebration,
    /// <summary>Days skipped in a row: the reason is asked before anything else.</summary>
    Reason,
    /// <summary>No habit yet.</summary>
    FirstRun,
    /// <summary>The settings sheet (the gear) over the habits.</summary>
    Settings,
}

/// <summary>The real Habits view over an in-memory store: habits started months ago, days done, missed and slipped.</summary>
internal static class HabitsDemo
{
    public static Control Create(FakeModule module, HabitsStage stage)
    {
        var habits = new MemorySynced<Habit>();
        var store = new HabitsStore(habits, new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>());
        if (stage != HabitsStage.FirstRun) Seed(store, habits, leaveGap: stage == HabitsStage.Reason);

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        settings.Get<HabitsSettings>(HabitsIds.ModuleId).Update(s => s.PlayCelebrations = true);
        var vm = new HabitsViewModel(store, settings, new AvaloniaUi(), new YesDialogs(), new HabitReminderService(store, settings),
            NullLogger<HabitsViewModel>.Instance);
        var view = new HabitsView(module, vm, new Host());

        switch (stage)
        {
            case HabitsStage.Report:
                view.ShowReport();
                break;
            case HabitsStage.Settings:
                view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => view.OpenSettings(focusAdd: false), DispatcherPriority.Background);
                break;
            case HabitsStage.MarkDay:
                vm.TapCardCommand.Execute(vm.ToDo.First(c => !c.IsQuit));
                break;
            case HabitsStage.Celebration:
                // After the page is up, so the confetti has a screen to fall over.
                view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (vm.HasPopup) return;
                    vm.TapCardCommand.Execute(vm.ToDo.First(c => !c.IsQuit));
                    vm.SaveMarkCommand.Execute(null);
                }, DispatcherPriority.Background);
                break;
        }
        return view;
    }

    private static void Seed(HabitsStore store, MemorySynced<Habit> habits, bool leaveGap)
    {
        var today = store.Today;
        var random = new Random(7);
        string Add(string title, HabitKind kind, int daysAgo, int order) => habits.Add(new Habit
        {
            Title = title,
            Kind = kind,
            CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            StartDay = today.AddDays(-daysAgo),
            Order = order,
        });

        // Read: steady, a streak of six days waiting for today.
        var read = Add("Đọc 20 trang sách", HabitKind.Build, 210, 0);
        Fill(store, read, today, 210, random, rate: 0.82, partial: 0.15);
        for (var d = 1; d <= 6; d++) store.Check(read, today.AddDays(-d));
        store.Uncheck(read, today);

        // Exercise: patchy, done in part today.
        var gym = Add("Tập thể dục 30 phút", HabitKind.Build, 150, 1);
        Fill(store, gym, today, 150, random, rate: 0.55, partial: 0.3);
        store.Check(gym, today.AddDays(-1));
        store.Check(gym, today, 60);

        // Vocabulary: good, with a two-day gap before today (asked about on the Reason stage).
        var vocab = Add("Học 15 từ vựng Aptis", HabitKind.Build, 95, 2);
        Fill(store, vocab, today, 95, random, rate: 0.7, partial: 0.1);
        store.Uncheck(vocab, today.AddDays(-1));
        store.Uncheck(vocab, today.AddDays(-2));
        store.Uncheck(vocab, today);

        // Water: done today.
        var water = Add("Uống 2 lít nước", HabitKind.Build, 40, 3);
        Fill(store, water, today, 40, random, rate: 0.9, partial: 0);
        store.Check(water, today);

        // Smoking: clean most days, a slip three weeks ago; yesterday not answered yet.
        var smoke = Add("Hút thuốc", HabitKind.Quit, 70, 4);
        for (var d = 70; d >= 2; d--)
        {
            var day = today.AddDays(-d);
            if (d != 21) store.Check(smoke, day);
        }
        store.WriteReason(smoke, today.AddDays(-21), today.AddDays(-21), "Đi nhậu với bạn cũ, mọi người hút nên mình làm một điếu.");

        // Every older run of skipped days already has its reason; the Reason stage keeps the newest one open.
        var reasons = new[]
        {
            "Đi công tác Đà Nẵng, về khách sạn muộn và mệt.", "Ốm hai hôm, sốt nhẹ.", "Deadline dự án, làm tới khuya.",
            "Về quê ăn cưới, không mang theo sách.", "Lười, xem phim cả buổi tối.",
        };
        var n = 0;
        foreach (var (id, habit) in store.Habits().Select(h => (h.Id, h.Value)))
        {
            for (var guard = 0; guard < 60; guard++)
            {
                var gaps = HabitLapses.Pending(id, habit, store.DoneDays(id), store.Lapses(id), today);
                var gap = leaveGap && id == vocab ? gaps.FirstOrDefault(g => g.To < today.AddDays(-2)) : gaps.FirstOrDefault();
                if (gap is null) break;
                store.WriteReason(id, gap.From, gap.To, reasons[n++ % reasons.Length]);
            }
        }
    }

    /// <summary>Marks the habit's past days: most of them done (some only in part), the rest missed.</summary>
    private static void Fill(HabitsStore store, string id, DateOnly today, int days, Random random, double rate, double partial)
    {
        for (var d = days; d >= 1; d--)
        {
            if (random.NextDouble() > rate) continue;
            var percent = random.NextDouble() < partial ? 25 * random.Next(1, 4) : 100;
            store.Check(id, today.AddDays(-d), percent);
        }
    }

    private sealed class Host : IHabitsPageHost
    {
        public void Opened() { }
    }
}
