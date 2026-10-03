namespace Helm.Modules.Missions;

/// <summary>One mission on the home-screen widget.</summary>
public sealed record MissionWidgetRow(string MissionId, string Title, string Step, string Progress, bool IsBehind, int Done = 0, int Total = 0)
{
    /// <summary>0–100 for the progress bar.</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);
}

/// <summary>
/// What the Android home-screen widget shows: the missions in progress (paused ones after them), each with the step it
/// is on and its progress. Platform-free, so it is tested here; the Android side only draws it.
/// </summary>
public sealed record MissionWidgetModel(IReadOnlyList<MissionWidgetRow> Rows, string EmptyText, int More)
{
    public const int MaxRows = 3;

    public bool IsEmpty => Rows.Count == 0;

    public static MissionWidgetModel Build(MissionsStore store, DateTimeOffset now, TimeZoneInfo zone)
    {
        var rows = new List<MissionWidgetRow>();
        var running = store.Missions()
            .Where(m => m.Value.Status is MissionStatus.Active or MissionStatus.Paused)
            .OrderBy(m => m.Value.Status == MissionStatus.Active ? 0 : 1)
            .ToList();
        foreach (var m in running.Take(MaxRows))
        {
            var steps = store.Steps(m.Id).Select(s => s.Value).ToList();
            var current = steps.FirstOrDefault(s => !s.IsDone);
            var pace = MissionPace.Compute(m.Value, steps, [], now, zone);
            var progress = $"{pace.Done}/{pace.Total}";
            if (m.Value.Status == MissionStatus.Paused) progress += " · paused";
            else if (pace.DaysOff != 0) progress += " · " + MissionsFormat.Pace(pace).ToLowerInvariant();
            rows.Add(new MissionWidgetRow(m.Id, m.Value.Title, current?.Title ?? "All steps done", progress,
                m.Value.Status == MissionStatus.Active && pace.DaysOff > 0, pace.Done, pace.Total));
        }
        var planned = store.Missions().Any(m => m.Value.Status == MissionStatus.Planned);
        var empty = planned ? "No mission in progress. Open Missions and press Start mission." : "No mission in progress. Open Missions to start one.";
        return new MissionWidgetModel(rows, empty, Math.Max(0, running.Count - MaxRows));
    }
}
