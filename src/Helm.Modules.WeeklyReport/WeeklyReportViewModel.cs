using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.SlackBot;

namespace Helm.Modules.WeeklyReport;

/// <summary>A project or a conversation the user may put in the report.</summary>
public sealed partial class PickRow : ObservableObject
{
    private readonly Action<PickRow> _changed;

    [ObservableProperty] private bool _isSelected;

    public PickRow(string key, string name, string detail, bool selected, Action<PickRow> changed)
    {
        Key = key;
        Name = name;
        Detail = detail;
        _isSelected = selected;
        _changed = changed;
    }

    /// <summary>"git:name" or "slack:#channel": what the Hidden list remembers.</summary>
    public string Key { get; }

    public string Name { get; }

    /// <summary>"12 commits · 3 days · about 9 h".</summary>
    public string Detail { get; }

    partial void OnIsSelectedChanged(bool value) => _changed(this);
}

/// <summary>
/// Weekly Report's page: choose the week, find the work (git and Slack), tick what goes in, have Claude write the report,
/// ask for changes until it is right, then copy it. Every version is kept while the page is open; finished reports are
/// kept on this PC.
/// </summary>
public sealed partial class WeeklyReportViewModel : ObservableObject
{
    private readonly WeekCollector _collector;
    private readonly WeeklyReportWriter _writer;
    private readonly ReportHistory _history;
    private readonly SlackBotEngine _slack;
    private readonly ISettingsStore<WeeklyReportSettings> _settings;
    private readonly List<string> _drafts = [];
    private WeekScan? _scan;
    private WeekRange? _scanned;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private WeekRange _week;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _slackNote;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasScan;
    [ObservableProperty] private string _emphasis = "";
    [ObservableProperty] private string _request = "";
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private bool _hasDraft;
    [ObservableProperty] private int _version;
    [ObservableProperty] private string _versionText = "";

    public WeeklyReportViewModel(WeekCollector collector, WeeklyReportWriter writer, ReportHistory history, SlackBotEngine slack,
        ISettingsStoreFactory settings)
    {
        _collector = collector;
        _writer = writer;
        _history = history;
        _slack = slack;
        _settings = settings.Get<WeeklyReportSettings>(WeeklyReportModule.ModuleId);
        Weeks = WeekRange.Choices(DateOnly.FromDateTime(DateTime.Today));
        _week = Weeks[0];
        foreach (var r in history.Load()) Past.Add(r);
    }

    public IReadOnlyList<WeekRange> Weeks { get; }

    public ObservableCollection<PickRow> Projects { get; } = [];

    public ObservableCollection<PickRow> Conversations { get; } = [];

    public ObservableCollection<SavedReport> Past { get; } = [];

    partial void OnWeekChanged(WeekRange value)
    {
        // Another week: what was found and written was for the old one.
        HasScan = false;
        Projects.Clear();
        Conversations.Clear();
        ClearDrafts();
        Status = "";
    }

    [RelayCommand]
    private async Task Find()
    {
        if (!Begin()) return;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var scan = await _collector.ScanAsync(Week, progress, _cts!.Token);
            _scan = scan;
            _scanned = Week;
            var hidden = _settings.Current.Hidden.ToHashSet();
            Projects.Clear();
            foreach (var p in scan.Projects.OrderByDescending(p => p.Commits.Count))
            {
                var days = p.Commits.Select(c => DateOnly.FromDateTime(c.When.LocalDateTime)).Distinct().Count();
                var detail = $"{p.Commits.Count} commits · {days} days · about {WorkTime.Hours(WorkTime.Total(p.Commits.Select(c => c.When)))}";
                Projects.Add(new PickRow("git:" + p.Name, p.Name, detail, !hidden.Contains("git:" + p.Name), Remember));
            }
            Conversations.Clear();
            foreach (var c in scan.Conversations)
                Conversations.Add(new PickRow("slack:" + c.Where, c.Where, $"{c.Messages.Count} messages", !hidden.Contains("slack:" + c.Where), Remember));
            SlackNote = scan.SlackNote;
            HasScan = true;
            Status = Projects.Count + Conversations.Count == 0
                ? "Nothing found for this week: check the folders and your git name on the settings page."
                : $"Found {Projects.Count} projects and {Conversations.Count} conversations. Tick what goes in, then Write the report.";
            ClearDrafts();
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private async Task Write()
    {
        if (Selected() is not { } data) return;
        if (_writer.Unavailable() is { } problem)
        {
            Status = problem;
            return;
        }
        if (!Begin()) return;
        try
        {
            Status = "Claude is writing the report… (a minute or two)";
            var text = await _writer.WriteAsync(data, Me(), _settings.Current.Notes, Emphasis, Model(), _cts!.Token);
            ClearDrafts();
            Add(text);
            Status = "Read it; ask for changes below, or Copy when it is right.";
        }
        catch (ClaudeCodeException ex)
        {
            Status = "Claude Code could not write it: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private async Task Revise()
    {
        if (Request.Trim().Length == 0 || !HasDraft || Selected() is not { } data) return;
        if (!Begin()) return;
        try
        {
            Status = "Claude is rewriting it…";
            var text = await _writer.ReviseAsync(data, Me(), _settings.Current.Notes, Draft, Request.Trim(), Model(), _cts!.Token);
            Add(text);
            Request = "";
            Status = "Here is the new version; Earlier goes back to the one before.";
        }
        catch (ClaudeCodeException ex)
        {
            Status = "Claude Code could not rewrite it: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Earlier() => Show(Version - 1);

    [RelayCommand]
    private void Later() => Show(Version + 1);

    /// <summary>The user copied the report: kept as this week's finished report (the text may have been edited by hand).</summary>
    public void Finished(string text)
    {
        if (_scanned is not { } week || text.Trim().Length == 0) return;
        Past.Clear();
        foreach (var r in _history.Save(new SavedReport(week.From, week.To, text.Trim(), DateTimeOffset.Now))) Past.Add(r);
        Status = "Copied, and kept with your past reports.";
    }

    /// <summary>The ticked projects and conversations of the scanned week, or null (with the reason in Status).</summary>
    internal WeekData? Selected()
    {
        if (_scan is not { } scan || _scanned is not { } week)
        {
            Status = "Find your work first.";
            return null;
        }
        var projects = Projects.Where(r => r.IsSelected).Select(r => scan.Projects.First(p => "git:" + p.Name == r.Key)).ToList();
        var conversations = Conversations.Where(r => r.IsSelected).Select(r => scan.Conversations.First(c => "slack:" + c.Where == r.Key)).ToList();
        if (projects.Count + conversations.Count == 0)
        {
            Status = "Tick at least one project or conversation.";
            return null;
        }
        return new WeekData(week.From, week.To, projects, conversations);
    }

    private void Remember(PickRow row) => _settings.Update(s =>
    {
        s.Hidden.Remove(row.Key);
        if (!row.IsSelected) s.Hidden.Add(row.Key);
    });

    private string Me() => _slack.MeName is { Length: > 0 } name ? name : Environment.UserName;

    private string? Model() => _settings.Current.Model is { Length: > 0 } m ? m : null;

    private bool Begin()
    {
        if (IsBusy) return false;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        return true;
    }

    private void End()
    {
        IsBusy = false;
        _cts?.Dispose();
        _cts = null;
    }

    private void Add(string text)
    {
        _drafts.Add(text);
        Show(_drafts.Count);
    }

    private void Show(int version)
    {
        if (version < 1 || version > _drafts.Count) return;
        Version = version;
        Draft = _drafts[version - 1];
        HasDraft = true;
        VersionText = $"Version {version} of {_drafts.Count}";
    }

    private void ClearDrafts()
    {
        _drafts.Clear();
        Draft = "";
        HasDraft = false;
        Version = 0;
        VersionText = "";
    }
}
