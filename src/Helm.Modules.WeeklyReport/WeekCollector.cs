using System.Text.RegularExpressions;
using Helm.Core.Settings;
using Helm.Modules.SlackBot;

namespace Helm.Modules.WeeklyReport;

/// <summary>A week, Monday to Sunday (or to today for this week).</summary>
public sealed record WeekRange(string Label, DateOnly From, DateOnly To)
{
    public DateTimeOffset Start => new(From.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(From.ToDateTime(TimeOnly.MinValue)));

    public DateTimeOffset End => new(To.ToDateTime(TimeOnly.MaxValue), TimeZoneInfo.Local.GetUtcOffset(To.ToDateTime(TimeOnly.MaxValue)));

    /// <summary>Last week, this week so far, and the week before last, for <paramref name="today"/>.</summary>
    public static IReadOnlyList<WeekRange> Choices(DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return
        [
            new($"Last week ({monday.AddDays(-7):dd/MM} – {monday.AddDays(-1):dd/MM})", monday.AddDays(-7), monday.AddDays(-1)),
            new($"This week ({monday:dd/MM} – {today:dd/MM})", monday, today),
            new($"Two weeks ago ({monday.AddDays(-14):dd/MM} – {monday.AddDays(-8):dd/MM})", monday.AddDays(-14), monday.AddDays(-8)),
        ];
    }
}

/// <summary>What the scan of a week found; <see cref="SlackNote"/> says why Slack is missing, if it is.</summary>
public sealed record WeekScan(IReadOnlyList<GitProject> Projects, IReadOnlyList<SlackWork> Conversations, string? SlackNote);

/// <summary>Finds the week's work: the user's commits in their repositories, and what they wrote on Slack (through Slack Bot).</summary>
public sealed partial class WeekCollector(GitActivity git, SlackBotEngine slack, ISettingsStoreFactory settings)
{
    private readonly ISettingsStore<WeeklyReportSettings> _settings = settings.Get<WeeklyReportSettings>(WeeklyReportModule.ModuleId);

    public async Task<WeekScan> ScanAsync(WeekRange week, IProgress<string>? progress, CancellationToken ct)
    {
        var s = _settings.Current;
        var folders = s.RepoFolders.Count > 0 ? s.RepoFolders : WeeklyReportSettings.UsualFolders();
        var authors = s.Authors.Count > 0 ? s.Authors : await git.DefaultAuthorsAsync(ct).ConfigureAwait(false);
        var projects = await git.ReadAsync(folders, authors, week.Start, week.End, progress, ct).ConfigureAwait(false);

        progress?.Report("Reading what you wrote on Slack…");
        if (slack.Api is not { } api || slack.Me is not { } me)
            return new WeekScan(projects, [], "Slack Bot is not connected: the report uses git only.");
        try
        {
            var found = await api.SearchMineAsync(me.UserId, week.From, week.To, ct).ConfigureAwait(false);
            var names = new Dictionary<string, string?>();
            foreach (Match tag in UserTag().Matches(string.Concat(found.Select(f => f.Message.Text))))
            {
                var id = tag.Groups[1].Value;
                if (!names.ContainsKey(id)) names[id] = await api.UserNameAsync(id, ct).ConfigureAwait(false);
            }
            var conversations = found
                .GroupBy(f => f.Where)
                .Select(g => new SlackWork(g.Key, g.Select(f => (SlackIds.Time(f.Message.Ts), SlackText.Plain(f.Message.Text, id => names.GetValueOrDefault(id)))).ToList()))
                .OrderByDescending(c => c.Messages.Count)
                .ToList();
            return new WeekScan(projects, conversations, null);
        }
        catch (SlackApiException ex)
        {
            return new WeekScan(projects, [], $"Slack could not be searched ({ex.Error}): the report uses git only.");
        }
    }

    [GeneratedRegex("<@([A-Z0-9]+)")]
    private static partial Regex UserTag();
}
