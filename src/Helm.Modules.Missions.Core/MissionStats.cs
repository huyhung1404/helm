using System.Globalization;
using System.Text;

namespace Helm.Modules.Missions;

/// <summary>Numbers across all missions, for the Statistics card.</summary>
public sealed record MissionStatsInfo(
    int StepsThisWeek,
    IReadOnlyList<int> StepsPerWeek,
    int StepsDone,
    double? PaceRatio,
    int MissionsDone,
    int MissionsWithDeadline,
    int MissionsOnTime,
    int MissionsRunning)
{
    /// <summary>"3, 5, 2, 4" — the last four weeks, oldest first.</summary>
    public string WeeksText => string.Join(", ", StepsPerWeek);
}

/// <summary>
/// Statistics and the CSV export across all missions. Weeks start on Monday (local time); a step's actual time is
/// from its start to its finish, against its estimate; skipped steps count in neither.
/// </summary>
public static class MissionStats
{
    public const int Weeks = 4;

    public static MissionStatsInfo Compute(MissionsStore store, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = MissionPace.Day(now, zone);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var perWeek = new int[Weeks];
        var steps = 0;
        double actual = 0, planned = 0;
        int done = 0, withDeadline = 0, onTime = 0, running = 0;
        foreach (var m in store.Missions())
        {
            if (m.Value.Status == MissionStatus.Active) running++;
            if (m.Value is { Status: MissionStatus.Completed, CompletedAt: { } finished })
            {
                done++;
                if (m.Value.Deadline is { } deadline)
                {
                    withDeadline++;
                    if (MissionPace.Day(finished, zone) <= deadline) onTime++;
                }
            }
            foreach (var s in store.Steps(m.Id).Select(x => x.Value).Where(s => s is { IsDone: true, Skipped: false }))
            {
                steps++;
                var day = MissionPace.Day(s.CompletedAt!.Value, zone);
                var week = (monday.DayNumber - day.DayNumber + 6) / 7; // 0 = this week
                if (day >= monday) week = 0;
                if (week is >= 0 and < Weeks) perWeek[Weeks - 1 - week]++;
                if (s.StartedAt is { } start)
                {
                    actual += Math.Max(0, (s.CompletedAt!.Value - start).TotalDays);
                    planned += s.EstimateDays;
                }
            }
        }
        return new MissionStatsInfo(perWeek[^1], perWeek, steps, planned > 0 ? actual / planned : null, done, withDeadline, onTime, running);
    }

    /// <summary>"Steps take 1.4× their plan", "Steps take about their plan", "Steps take 0.7× their plan"; "" with none.</summary>
    public static string PaceText(MissionStatsInfo stats) => stats.PaceRatio switch
    {
        null => "",
        >= 0.9 and <= 1.1 => "Steps take about as long as planned",
        { } r => $"Steps take {r.ToString("0.0", CultureInfo.CurrentCulture)}× their plan",
    };

    private static readonly string[] Header =
    [
        "mission", "mission_status", "phase", "step_number", "step", "status", "planned_days", "started", "completed",
        "actual_days", "done_when", "note", "mission_id", "step_id",
    ];

    /// <summary>Every step of every mission (RFC 4180, ISO 8601 times, invariant numbers), for a spreadsheet.</summary>
    public static string Csv(MissionsStore store)
    {
        static string Time(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "";
        static string Number(double? n) => n is { } v ? Math.Round(v, 2).ToString(CultureInfo.InvariantCulture) : "";
        var b = new StringBuilder();
        Row(b, Header);
        foreach (var m in store.Missions())
        {
            var steps = store.Steps(m.Id);
            var current = steps.FirstOrDefault(s => !s.Value.IsDone)?.Id;
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i].Value;
                var status = s.Skipped ? "skipped" : s.IsDone ? "done" : steps[i].Id == current && m.Value.Status == MissionStatus.Active ? "current" : "open";
                double? actual = s is { IsDone: true, Skipped: false, StartedAt: { } st } ? (s.CompletedAt!.Value - st).TotalDays : null;
                Row(b,
                [
                    m.Value.Title, m.Value.Status.ToString().ToLowerInvariant(), m.Value.Phase(s.PhaseKey)?.Title ?? "",
                    (i + 1).ToString(CultureInfo.InvariantCulture), s.Title, status, Number(s.EstimateDays),
                    Time(s.StartedAt), Time(s.CompletedAt), Number(actual), s.DoneWhen, s.Note, m.Id, steps[i].Id,
                ]);
            }
        }
        return b.ToString();
    }

    private static void Row(StringBuilder b, IEnumerable<string> cells)
    {
        b.AppendJoin(',', cells.Select(Quote));
        b.Append("\r\n");
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
}
