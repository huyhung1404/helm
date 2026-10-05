using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Modules.Missions;

/// <summary>Text shown for days, dates, pace and status (the same on both apps).</summary>
public static class MissionsFormat
{
    /// <summary>"12 Oct", or "12 Oct 2027" outside this year.</summary>
    public static string Date(DateOnly day, DateOnly today) =>
        day.ToString(day.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Date(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone) =>
        Date(MissionPace.Day(at, zone), MissionPace.Day(now, zone));

    /// <summary>"½ day", "1 day", "2.5 days", "3 days".</summary>
    public static string Days(double days)
    {
        if (days <= 0.5) return "½ day";
        var rounded = Math.Round(days * 2) / 2;
        var text = rounded.ToString(rounded % 1 == 0 ? "0" : "0.0", CultureInfo.CurrentCulture);
        return rounded == 1 ? "1 day" : $"{text} days";
    }

    /// <summary>How long a step took: "under an hour", "5 hours", "2 days".</summary>
    public static string Took(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours < 1) return "under an hour";
        if (span.TotalHours < 24) return span.TotalHours < 2 ? "1 hour" : $"{(int)span.TotalHours} hours";
        var days = (int)Math.Round(span.TotalDays);
        return days == 1 ? "1 day" : $"{days} days";
    }

    /// <summary>"2 days behind", "on track", "1 day ahead".</summary>
    public static string Pace(MissionPaceInfo pace) => pace.DaysOff switch
    {
        0 => "On track",
        1 => "1 day behind",
        > 1 => $"{pace.DaysOff} days behind",
        -1 => "1 day ahead",
        _ => $"{-pace.DaysOff} days ahead",
    };

    public static string Status(MissionStatus status) => status switch
    {
        MissionStatus.Planned => "Not started",
        MissionStatus.Active => "In progress",
        MissionStatus.Paused => "Paused",
        MissionStatus.Completed => "Completed",
        MissionStatus.Abandoned => "Abandoned",
        _ => status.ToString(),
    };

    /// <summary>"Projected 14 Mar, 13 days after the deadline" and the like; "" when there is nothing to project.</summary>
    public static string Projection(MissionPaceInfo pace, DateOnly? deadline, DateOnly today)
    {
        if (pace.ProjectedFinish is not { } finish) return "";
        var text = $"Projected to finish {Date(finish, today)}";
        if (deadline is not { } d) return text;
        var late = finish.DayNumber - d.DayNumber;
        return late switch
        {
            > 1 => $"{text}, {late} days after the deadline",
            1 => $"{text}, 1 day after the deadline",
            0 => $"{text}, on the deadline",
            -1 => $"{text}, 1 day before the deadline",
            _ => $"{text}, {-late} days before the deadline",
        };
    }

    /// <summary>"3-day streak" ("" for none).</summary>
    public static string Streak(int days) => days switch { <= 0 => "", 1 => "1-day streak", _ => $"{days}-day streak" };

    // ---- Summary -------------------------------------------------------------------------------------------------

    /// <summary>The end-of-mission summary (Markdown): numbers, the reward, and every step with its date and note.</summary>
    public static string Summary(Mission mission, IReadOnlyList<MissionStep> steps, MissionPaceInfo pace, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = MissionPace.Day(now, zone);
        var b = new StringBuilder();
        b.AppendLine($"# {(mission.Status == MissionStatus.Completed ? "Mission complete" : "Mission")}: {mission.Title}");
        b.AppendLine();
        if (mission.Goal.Length > 0) b.AppendLine($"**Goal:** {mission.Goal}  ");
        var dates = new List<string>();
        if (mission.StartedAt is { } s) dates.Add($"started {Date(s, now, zone)}");
        if (mission.CompletedAt is { } c) dates.Add($"finished {Date(c, now, zone)}");
        if (mission.StartedAt is not null) dates.Add($"{Days(pace.ElapsedDays)} (planned {Days(pace.PlannedDays)})");
        if (mission.Deadline is { } d) dates.Add($"deadline {Date(d, today)}");
        if (dates.Count > 0) b.AppendLine($"**When:** {string.Join(" · ", dates)}  ");
        var counts = $"{pace.Done - pace.Skipped} of {pace.Total} steps done";
        if (pace.Skipped > 0) counts += $", {pace.Skipped} skipped";
        if (pace.LongestStreak > 1) counts += $" · longest streak {pace.LongestStreak} days";
        b.AppendLine($"**Steps:** {counts}  ");
        if (mission.Reward.Length > 0) b.AppendLine($"**Reward:** {mission.Reward}{(mission.RewardClaimedAt is { } r ? $" (taken {Date(r, now, zone)})" : "")}  ");
        foreach (var phase in mission.Phases)
        {
            b.AppendLine();
            b.AppendLine($"## {phase.Title}");
            foreach (var step in steps.Where(x => x.PhaseKey == phase.Key))
            {
                var mark = step.Skipped ? "⤼" : step.IsDone ? "✓" : "○";
                var line = $"- {mark} {step.Title}";
                if (step.CompletedAt is { } at)
                {
                    line += $" — {Date(at, now, zone)}";
                    if (!step.Skipped && step.StartedAt is { } st) line += $" ({Took(at - st)})";
                }
                if (step.Note.Length > 0) line += $": {step.Note.Replace('\n', ' ')}";
                b.AppendLine(line);
            }
        }
        return b.ToString().TrimEnd() + "\n";
    }

    // ---- JSON ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The mission in the import format (plus each step's dates), for a backup, sharing, or handing back to an AI.
    /// <see cref="MissionImport"/> reads it back (the dates are ignored there).
    /// </summary>
    public static string Json(Mission mission, IReadOnlyList<MissionStep> steps)
    {
        static string? Iso(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
        var root = new JsonObject
        {
            ["helmMission"] = MissionImport.Version,
            ["title"] = mission.Title,
            ["goal"] = mission.Goal,
            ["deadline"] = mission.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["reward"] = mission.Reward,
            ["note"] = mission.Note.Length > 0 ? mission.Note : null,
            ["status"] = mission.Status.ToString().ToLowerInvariant(),
            ["startedAt"] = Iso(mission.StartedAt),
            ["completedAt"] = Iso(mission.CompletedAt),
        };
        var phases = new JsonArray();
        foreach (var phase in mission.Phases)
        {
            var list = new JsonArray();
            foreach (var s in steps.Where(x => x.PhaseKey == phase.Key))
            {
                var step = new JsonObject
                {
                    ["title"] = s.Title,
                    ["description"] = s.Description.Length > 0 ? s.Description : null,
                    ["doneWhen"] = s.DoneWhen.Length > 0 ? s.DoneWhen : null,
                    ["estimateDays"] = s.EstimateDays,
                };
                if (s.Checklist.Count > 0) step["checklist"] = new JsonArray(s.Checklist.Select(c => (JsonNode?)c.Text).ToArray());
                if (MissionResources.Of(s) is { Count: > 0 } resources) step["resources"] = new JsonArray(resources.Select(r => (JsonNode?)MissionResources.ToJson(r)).ToArray());
                if (s.CompletedAt is not null) step[s.Skipped ? "skippedAt" : "completedAt"] = Iso(s.CompletedAt);
                if (s.Note.Length > 0) step["note"] = s.Note;
                list.Add(step);
            }
            phases.Add(new JsonObject { ["title"] = phase.Title, ["reward"] = phase.Reward, ["steps"] = list });
        }
        root["phases"] = phases;
        // Drop the empty optional fields.
        foreach (var key in root.Where(p => p.Value is null).Select(p => p.Key).ToList()) root.Remove(key);
        foreach (var step in phases.SelectMany(p => p!["steps"]!.AsArray()).OfType<JsonObject>())
            foreach (var key in step.Where(p => p.Value is null).Select(p => p.Key).ToList()) step.Remove(key);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
