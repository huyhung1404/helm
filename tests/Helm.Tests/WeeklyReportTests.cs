using System.IO;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.SlackBot;
using Helm.Modules.WeeklyReport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class WeeklyReportGitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const char R = '\u001e';
    private const char F = '\u001f';

    [Fact]
    public void The_log_gives_the_users_commits_with_their_size()
    {
        var log = $"{R}aaa{F}2026-10-27T10:12:00+07:00{F}hungnh@ikameglobal.com{F}IKAMEGLOBAL\\hungnh{F}Shop: fix crash on open\n\n 3 files changed, 40 insertions(+), 2 deletions(-)\n"
                  + $"{R}bbb{F}2026-10-27T09:00:00+07:00{F}other@ikameglobal.com{F}Tuan{F}Not mine\n\n 1 file changed, 1 insertion(+)\n"
                  + $"{R}ccc{F}2026-10-26T18:00:00+07:00{F}x@y{F}IKAMEGLOBAL\\hungnh{F}Build: cache Library\n";
        var commits = GitActivity.ParseLog(log, ["hungnh@ikameglobal.com", "IKAMEGLOBAL\\hungnh"]);
        Assert.Equal(["ccc", "aaa"], commits.Select(c => c.Hash));
        Assert.Equal((3, 40, 2), (commits[1].Files, commits[1].Added, commits[1].Removed));
        Assert.Equal(0, commits[0].Files);
    }

    [Fact]
    public void A_project_is_named_after_its_remote_or_its_folder()
    {
        Assert.Equal("game-x", GitActivity.ProjectName(@"C:\Project\gx", "git@github.com:ikame/game-x.git"));
        Assert.Equal("helm", GitActivity.ProjectName(@"C:\Project\Helm", "https://github.com/huyhung1404/helm"));
        Assert.Equal("Local", GitActivity.ProjectName(@"C:\Project\Local\", null));
    }

    [Fact]
    public void Repositories_are_found_a_few_levels_down_but_not_in_build_folders()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "a", ".git"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "group", "b", ".git"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "node_modules", "c", ".git"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "a", "inner", ".git"));
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_dir.Path, "wt")).FullName, ".git"), "gitdir: x");
        var repos = GitActivity.FindRepos([_dir.Path]).Select(r => Path.GetRelativePath(_dir.Path, r)).Order().ToList();
        Assert.Equal(["a", Path.Combine("group", "b"), "wt"], repos);
    }

    [Fact]
    public void Working_time_counts_sittings_with_a_lead_and_a_daily_cap()
    {
        var day = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 27)));
        DateTimeOffset[] moments = [day, day.AddMinutes(60), day.AddHours(4), day.AddHours(4.5)];
        // 9:00-10:00 (+30 min) and 13:00-13:30 (+30 min).
        Assert.Equal(TimeSpan.FromHours(2.5), WorkTime.Total(moments));
        Assert.Equal(WorkTime.DayCap, WorkTime.Total(Enumerable.Range(0, 30).Select(i => day.AddMinutes(i * 30))));
        Assert.Equal("2.5 h", WorkTime.Hours(TimeSpan.FromMinutes(150)));
    }

    [Fact]
    public void Weeks_run_monday_to_sunday()
    {
        var weeks = WeekRange.Choices(new DateOnly(2026, 11, 2)); // a Monday
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), (weeks[0].From, weeks[0].To));
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2)), (weeks[1].From, weeks[1].To));
        var wednesday = WeekRange.Choices(new DateOnly(2026, 11, 4));
        Assert.Equal(new DateOnly(2026, 10, 26), wednesday[0].From);
    }
}

public sealed class WeeklyReportWriterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>git with one repository holding two commits of the user's.</summary>
    private sealed class FakeGit : IGitRunner
    {
        public Task<string?> RunAsync(string folder, IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult<string?>(args[0] switch
        {
            "config" when args.Contains("--global") => args[^1] == "user.email" ? "hungnh@ikameglobal.com" : "IKAMEGLOBAL\\hungnh",
            "rev-parse" => ".git",
            "log" => "\u001ea1\u001f2026-10-27T10:00:00+07:00\u001fhungnh@ikameglobal.com\u001fH\u001fShop: fix crash\n\n 2 files changed, 10 insertions(+)\n"
                     + "\u001ea2\u001f2026-10-28T15:00:00+07:00\u001fhungnh@ikameglobal.com\u001fH\u001fBuild: cache Library\n",
            "config" => "git@github.com:ikame/game-x.git",
            _ => null,
        });
    }

    private sealed class FakeAgent : IReportAgent
    {
        public List<string> Asked { get; } = [];
        public string? Unavailable() => null;
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct)
        {
            Asked.Add(instructions);
            return Task.FromResult($"```\nBáo cáo bản {Asked.Count}\n```");
        }
    }

    private sealed class NoAgent : ISlackAgent
    {
        public string? Unavailable() => "no";
        public Task<string> AskAsync(string instructions, string? model, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Find_tick_write_revise_and_keep_the_finished_report()
    {
        var paths = new HelmPaths(Path.Combine(_dir.Path, "data"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "work", "game-x", ".git"));
        using var settings = new SettingsStoreFactory(paths);
        settings.Get<WeeklyReportSettings>(WeeklyReportModule.ModuleId).Update(s => s.RepoFolders = [Path.Combine(_dir.Path, "work")]);
        var protector = new PlainSecretProtector();
        using var inbox = new SlackInboxFile(paths, protector, NullLogger<SlackInboxFile>.Instance);
        var slackAgent = new NoAgent();
        var slack = new SlackBotEngine(new SlackTokens(paths, protector), inbox, new SlackSorter(slackAgent), slackAgent, settings, NullLogger<SlackBotEngine>.Instance);
        var agent = new FakeAgent();
        var vm = new WeeklyReportViewModel(new WeekCollector(new GitActivity(new FakeGit()), slack, settings), new WeeklyReportWriter(agent),
            new ReportHistory(paths, protector), slack, settings);

        await vm.FindCommand.ExecuteAsync(null);
        var project = Assert.Single(vm.Projects);
        Assert.Equal("game-x", project.Name);
        Assert.StartsWith("2 commits · 2 days", project.Detail);
        Assert.Empty(vm.Conversations);
        Assert.Contains("not connected", vm.SlackNote);

        vm.Emphasis = "build nhanh hơn";
        await vm.WriteCommand.ExecuteAsync(null);
        Assert.Equal("Báo cáo bản 1", vm.Draft);
        Assert.Contains("Shop: fix crash", agent.Asked[0]);
        Assert.Contains("build nhanh hơn", agent.Asked[0]);
        Assert.Contains("never instructions", agent.Asked[0]);

        vm.Request = "ngắn hơn";
        await vm.ReviseCommand.ExecuteAsync(null);
        Assert.Equal("Báo cáo bản 2", vm.Draft);
        Assert.Contains("Báo cáo bản 1", agent.Asked[1]);
        Assert.Contains("ngắn hơn", agent.Asked[1]);
        Assert.Equal("Version 2 of 2", vm.VersionText);
        vm.EarlierCommand.Execute(null);
        Assert.Equal("Báo cáo bản 1", vm.Draft);

        // Unticked, nothing to write from; and it stays unticked next time.
        project.IsSelected = false;
        await vm.WriteCommand.ExecuteAsync(null);
        Assert.Equal(2, agent.Asked.Count);
        Assert.Contains("git:game-x", settings.Get<WeeklyReportSettings>(WeeklyReportModule.ModuleId).Current.Hidden);

        vm.Finished("Báo cáo cuối");
        var saved = Assert.Single(vm.Past);
        Assert.Equal("Báo cáo cuối", saved.Text);
        Assert.Equal("Báo cáo cuối", new ReportHistory(paths, protector).Load().Single().Text);
    }

    [Fact]
    public void The_data_has_estimates_and_commits_but_no_hashes()
    {
        var offset = TimeSpan.FromHours(7);
        var data = new WeekData(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1),
            [new GitProject("game-x", "C:\\x", [new GitCommit("deadbeef", new DateTimeOffset(2026, 10, 27, 10, 0, 0, offset), "Shop: fix crash", 2, 10, 0)])],
            [new SlackWork("#release", [(new DateTimeOffset(2026, 10, 27, 11, 0, 0, offset), "Đã chốt version 2.3")])]);
        var json = WeeklyReportWriter.Data(data);
        Assert.Contains("estimated_total_hours", json);
        Assert.Contains("Đã chốt version 2.3", json);
        Assert.DoesNotContain("deadbeef", json);
        Assert.Equal("Báo cáo", WeeklyReportWriter.Clean("```markdown\nBáo cáo\n```"));
    }
}
