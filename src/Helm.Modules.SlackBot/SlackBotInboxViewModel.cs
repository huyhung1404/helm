using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

/// <summary>One message on the overlay or the SlackBot page.</summary>
public sealed class InboxItemViewModel(InboxItem item, string link)
{
    public string Id => item.Id;

    public string Link => link;

    public string Where => item.Where;

    public string From => item.From;

    public string When => item.Time.ToLocalTime() is var t && t.Date == DateTime.Today
        ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
        : t.ToString("ddd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Claude's line, or the start of the message while it is not sorted.</summary>
    public string Headline => item.Summary ?? SlackText.Clip(item.Text, 160);

    /// <summary>The message itself, under Claude's line.</summary>
    public string Text => SlackText.Clip(item.Text, 600);

    public bool HasSummary => item.Summary is not null;

    /// <summary>For the user, not sorted yet (or Claude Code is not working): shown as it is.</summary>
    public bool IsUnsorted => item.Level == InboxLevel.Pending;

    public string HandledText => item.Handled switch
    {
        HandledBy.Replied => "You replied",
        HandledBy.Reacted => "You reacted",
        HandledBy.Dismissed => "Marked done",
        _ => "",
    };
}

/// <summary>The worth-knowing messages of one channel or DM.</summary>
public sealed class InboxGroupViewModel(string where, IReadOnlyList<InboxItemViewModel> items)
{
    public string Where => where;

    public IReadOnlyList<InboxItemViewModel> Items => items;

    public string Count => items.Count == 1 ? "1 message" : $"{items.Count} messages";
}

/// <summary>
/// What SlackBot has for the user, for the overlay and the SlackBot page (both bind this singleton): the to-do list,
/// the worth-knowing report grouped by channel, and what was handled in the last day. Rebuilt from the inbox shortly
/// after it changes (a catch-up changes it many times in a row).
/// </summary>
public sealed partial class SlackBotInboxViewModel : ObservableObject
{
    private static readonly TimeSpan HandledShown = TimeSpan.FromDays(1);

    private readonly SlackBotEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly IServiceProvider _services;
    private int _refreshQueued;
    private bool _shown;

    [ObservableProperty] private int _toDoCount;
    [ObservableProperty] private int _reportCount;
    [ObservableProperty] private bool _hasOverlay;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasToDo;
    [ObservableProperty] private bool _hasKnowing;
    [ObservableProperty] private bool _hasSortProblem;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _sortProblem;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private bool _isSorting;

    public SlackBotInboxViewModel(SlackBotEngine engine, IUiDispatcher ui, IProcessLauncher launcher, IServiceProvider services)
    {
        _engine = engine;
        _ui = ui;
        _launcher = launcher;
        _services = services;
        engine.Inbox.Changed += (_, _) => QueueRefresh();
        engine.Changed += (_, _) => QueueRefresh();
        Refresh();
    }

    public ObservableCollection<InboxItemViewModel> ToDo { get; } = [];

    /// <summary>Worth knowing, unread, by channel.</summary>
    public ObservableCollection<InboxGroupViewModel> Knowing { get; } = [];

    public ObservableCollection<InboxItemViewModel> Handled { get; } = [];

    /// <summary>Raised on the UI thread when something new lands on the to-do list.</summary>
    public event EventHandler? NewToDo;

    [RelayCommand]
    private void Open(InboxItemViewModel? item)
    {
        if (item is not null) _launcher.OpenUrl(item.Link);
    }

    [RelayCommand]
    private void Done(InboxItemViewModel? item)
    {
        if (item is not null) _engine.Inbox.Dismiss(item.Id);
    }

    [RelayCommand]
    private void Restore(InboxItemViewModel? item)
    {
        if (item is not null) _engine.Inbox.Restore(item.Id);
    }

    /// <summary>Claude put it on the to-do list but it only needs knowing.</summary>
    [RelayCommand]
    private void OnlyKnow(InboxItemViewModel? item)
    {
        if (item is not null) _engine.Inbox.Relevel(item.Id, InboxLevel.Know);
    }

    /// <summary>Claude put it under worth knowing but the user has to act on it.</summary>
    [RelayCommand]
    private void NeedsMe(InboxItemViewModel? item)
    {
        if (item is not null) _engine.Inbox.Relevel(item.Id, InboxLevel.Act);
    }

    [RelayCommand]
    private void MarkAllRead() => _engine.Inbox.MarkRead(Knowing.SelectMany(g => g.Items).Select(i => i.Id));

    [RelayCommand]
    private void MarkGroupRead(InboxGroupViewModel? group)
    {
        if (group is not null) _engine.Inbox.MarkRead(group.Items.Select(i => i.Id));
    }

    [RelayCommand]
    private async Task SortNow()
    {
        IsSorting = true;
        try
        {
            await _engine.SortNowAsync();
        }
        finally
        {
            IsSorting = false;
        }
    }

    [RelayCommand]
    private Task Reconnect() => _engine.RestartAsync();

    /// <summary>The SlackBot page, in front (from the overlay).</summary>
    [RelayCommand]
    private void ShowPage() => _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(SlackBotContentPage));

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Delay(300).ContinueWith(_ => _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }), TaskScheduler.Default);
    }

    private void Refresh()
    {
        var items = _engine.Inbox.Snapshot();
        var url = _engine.Me?.Url ?? _engine.Inbox.WorkspaceUrl ?? "https://slack.com/";
        InboxItemViewModel View(InboxItem i) => new(i, SlackText.Permalink(url, i.Channel, i.Ts, i.ThreadTs));

        var before = ToDo.Select(i => i.Id).ToHashSet();
        Replace(ToDo, items.Where(i => i.IsToDo).Select(View));
        var lastReport = _engine.Inbox.LastDigest;
        var knowing = items.Where(i => i.Level == InboxLevel.Know && i.Handled == HandledBy.None && !i.Read).ToList();
        Replace(Knowing, knowing
            .GroupBy(i => i.Where)
            .OrderByDescending(g => g.Max(i => i.Time))
            .Select(g => new InboxGroupViewModel(g.Key, g.Select(View).ToList())));
        var since = DateTimeOffset.UtcNow - HandledShown;
        Replace(Handled, items
            .Where(i => i.Handled != HandledBy.None && i.HandledAt > since && i.Level is InboxLevel.Act or InboxLevel.Pending)
            .OrderByDescending(i => i.HandledAt)
            .Select(View));

        ToDoCount = ToDo.Count;
        // Worth knowing shows on screen at the report times only: what came before the last one.
        ReportCount = lastReport is { } r ? knowing.Count(i => i.Time <= r) : 0;
        ReportTitle = lastReport is { } at
            ? $"{ReportCount} worth knowing ({at.ToLocalTime():HH:mm} report)"
            : "";
        HasToDo = ToDoCount > 0;
        HasReport = ReportCount > 0;
        HasOverlay = HasToDo || HasReport;
        Status = _engine.Status is { Length: > 0 } status ? status : _engine.State == SlackBotState.Off ? "Not connected (SlackBot is off)." : "";
        SortProblem = _engine.SortProblem;
        HasSortProblem = SortProblem is not null;
        HasKnowing = Knowing.Count > 0;
        IsConnected = _engine.State == SlackBotState.Connected;
        // Not for what was already there when Helm started.
        if (_shown && ToDo.Any(i => !before.Contains(i.Id))) NewToDo?.Invoke(this, EventArgs.Empty);
        _shown = true;
    }

    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items)
    {
        list.Clear();
        foreach (var item in items) list.Add(item);
    }
}
