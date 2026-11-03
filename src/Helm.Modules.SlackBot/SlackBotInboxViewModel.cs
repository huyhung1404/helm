using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.SlackBot;

/// <summary>
/// One card on the overlay or the SlackBot page: a message, or the messages still to do in one thread (or one DM),
/// shown as the newest (<paramref name="item"/>) with the others under it.
/// </summary>
public sealed class InboxItemViewModel(InboxItem item, string link, DateTimeOffset? now = null, IReadOnlyList<InboxItem>? thread = null)
{
    /// <summary>Every message of the card, oldest first.</summary>
    private readonly IReadOnlyList<InboxItem> _all = thread is { Count: > 0 } t ? t : [item];

    /// <summary>"Waiting 2 h" on a message still to do after an hour (the card's oldest), else empty.</summary>
    public string Waiting { get; } = WaitingText(thread is { Count: > 0 } t ? t[0] : item, now ?? DateTimeOffset.UtcNow);

    public bool HasWaiting => Waiting.Length > 0;

    internal static string WaitingText(InboxItem item, DateTimeOffset now)
    {
        if (!item.IsToDo) return "";
        var hours = (int)Math.Floor((now - item.Time).TotalHours);
        return hours < 1 ? "" : hours < 24 ? $"Waiting {hours} h" : $"Waiting {hours / 24} d";
    }

    /// <summary>The card's first message: it stays the same while more come into the thread.</summary>
    public string Id => _all[0].Id;

    /// <summary>Every message of the card: Done, Only know and the like act on all of them.</summary>
    public IReadOnlyList<string> Ids => [.. _all.Select(i => i.Id)];

    public int Count => _all.Count;

    /// <summary>"3 messages in this thread", or empty for one.</summary>
    public string CountText => Count == 1 ? "" : item.Kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage
        ? $"{Count} messages" : $"{Count} messages in this thread";

    public bool HasCount => Count > 1;

    /// <summary>The messages under the headline: once Claude wrote the headline, or always for a thread.</summary>
    public bool ShowText => HasSummary || HasCount;

    public string Link => link;

    public string Where => item.Where;

    public string From => item.From;

    public string When => item.Time.ToLocalTime() is var t && t.Date == DateTime.Today
        ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
        : t.ToString("ddd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Claude's line, or the start of the message while it is not sorted.</summary>
    public string Headline => item.Summary ?? SlackText.Clip(item.Text, 160);

    /// <summary>The message itself, under Claude's line; for a thread, its latest messages, each on a line (who wrote it when several did).</summary>
    public string Text => Count == 1
        ? SlackText.Clip(item.Text, 600)
        : string.Join("\n", _all.TakeLast(MaxLines).Select(i =>
            (_all.Select(a => a.From).Distinct().Count() > 1 ? i.From + ": " : "• ") + SlackText.Clip(i.Text, 200)));

    /// <summary>Messages of a thread shown on its card.</summary>
    internal const int MaxLines = 6;

    public bool HasSummary => item.Summary is not null;

    /// <summary>Claude's line alone (null while not sorted).</summary>
    public string? Summary => item.Summary;

    /// <summary>For a phone alert: who, where, Claude's line, the link, how long it waits.</summary>
    public SlackAlertLine Alert => new(From, Where, Summary, Link, Waiting);

    /// <summary>What Slack Bot answered for the user (the latest in the card), or null.</summary>
    public string? BotReply => _all.LastOrDefault(i => i.BotReply is not null)?.BotReply;

    public bool HasBotReply => BotReply is not null;

    /// <summary>Changes when anything shown changes: the lists replace a row only then.</summary>
    internal string Version => string.Join("/", _all.Select(i => $"{i.Id}|{i.Level}|{i.Summary}|{i.Handled}|{i.BotReply}|{i.Text}|{i.Read}"))
                               + $"|{link}|{Waiting}";

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

/// <summary>The worth-knowing messages of one channel or DM (kept as the messages come and go).</summary>
public sealed partial class InboxGroupViewModel : ObservableObject
{
    [ObservableProperty] private string _count = "";

    public InboxGroupViewModel(string where)
    {
        Where = where;
        Items.CollectionChanged += (_, _) => Count = Items.Count == 1 ? "1 message" : $"{Items.Count} messages";
    }

    public string Where { get; }

    public ObservableCollection<InboxItemViewModel> Items { get; } = [];
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
    private readonly ISettingsStore<SlackBotSettings> _settings;
    private readonly Timer _clock;
    private DateTimeOffset _lastRemind = DateTimeOffset.UtcNow;
    private int _refreshQueued;
    private bool _shown;

    [ObservableProperty] private int _toDoCount;
    [ObservableProperty] private int _reportCount;
    [ObservableProperty] private bool _hasOverlay;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasToDo;
    [ObservableProperty] private InboxItemViewModel? _replyTo;
    [ObservableProperty] private string _replyText = "";
    [ObservableProperty] private string? _replyProblem;
    [ObservableProperty] private bool _isSendingReply;
    [ObservableProperty] private bool _hasKnowing;
    [ObservableProperty] private bool _hasSortProblem;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _sortProblem;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private bool _isSorting;

    public SlackBotInboxViewModel(SlackBotEngine engine, IUiDispatcher ui, IProcessLauncher launcher, IServiceProvider services,
        ISettingsStoreFactory settings)
    {
        _settings = settings.Get<SlackBotSettings>(SlackBotModule.ModuleId);
        // Every minute: "Waiting 2 h" moves on, and what waits too long is brought back (Remind).
        _clock = new Timer(_ => _ui!.Post(Tick), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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

    /// <summary>Raised on the UI thread with what just landed on the to-do list (for the phone).</summary>
    public event EventHandler<IReadOnlyList<SlackAlertLine>>? NewToDoLines;

    /// <summary>What waits on the list for an hour or more, oldest first (for the phone).</summary>
    public IReadOnlyList<SlackAlertLine> WaitingLines() => ToDo.Where(i => i.HasWaiting).Select(i => i.Alert).ToList();

    /// <summary>Raised on the UI thread when something has waited too long: bring the list back.</summary>
    public event EventHandler? Remind;

    /// <summary>
    /// A reminder is due: a message on the list has waited at least <paramref name="every"/>, and the last reminder was
    /// at least that long ago.
    /// </summary>
    internal static bool RemindDue(IEnumerable<DateTimeOffset> waitingSince, DateTimeOffset now, DateTimeOffset last, TimeSpan every) =>
        every > TimeSpan.Zero && now - last >= every && waitingSince.Any(t => now - t >= every);

    private void Tick()
    {
        Refresh();
        var every = TimeSpan.FromMinutes(_settings.Current.RemindEveryMinutes);
        var now = DateTimeOffset.UtcNow;
        if (!RemindDue(_engine.Inbox.Snapshot().Where(i => i.IsToDo).Select(i => i.Time), now, _lastRemind, every)) return;
        _lastRemind = now;
        Remind?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Open(InboxItemViewModel? item)
    {
        if (item is not null) _launcher.OpenUrl(item.Link);
    }

    /// <summary>Opens the reply box for <paramref name="item"/> (the overlay takes the keyboard while it is open).</summary>
    [RelayCommand]
    private void StartReply(InboxItemViewModel? item)
    {
        if (item is null) return;
        ReplyProblem = null;
        if (ReplyTo?.Id != item.Id) ReplyText = "";
        ReplyTo = item;
    }

    [RelayCommand]
    private void CancelReply()
    {
        ReplyTo = null;
        ReplyText = "";
        ReplyProblem = null;
    }

    /// <summary>Sends the reply as the user; the message is then answered and leaves the list.</summary>
    [RelayCommand]
    private async Task SendReply()
    {
        if (ReplyTo is not { } item || IsSendingReply || ReplyText.Trim().Length == 0) return;
        IsSendingReply = true;
        try
        {
            ReplyProblem = await _engine.ReplyAsUserAsync(item.Id, ReplyText, CancellationToken.None);
            if (ReplyProblem is null) CancelReply();
        }
        finally
        {
            IsSendingReply = false;
        }
    }

    [RelayCommand]
    private void Done(InboxItemViewModel? item)
    {
        if (item is null) return;
        foreach (var id in item.Ids) _engine.Inbox.Dismiss(id);
    }

    [RelayCommand]
    private void Restore(InboxItemViewModel? item)
    {
        if (item is null) return;
        foreach (var id in item.Ids) _engine.Inbox.Restore(id);
    }

    /// <summary>Claude put it on the to-do list but it only needs knowing.</summary>
    [RelayCommand]
    private void OnlyKnow(InboxItemViewModel? item)
    {
        if (item is null) return;
        foreach (var id in item.Ids) _engine.Inbox.Relevel(id, InboxLevel.Know);
    }

    /// <summary>Claude put it under worth knowing but the user has to act on it.</summary>
    [RelayCommand]
    private void NeedsMe(InboxItemViewModel? item)
    {
        if (item is null) return;
        foreach (var id in item.Ids) _engine.Inbox.Relevel(id, InboxLevel.Act);
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
        // Catching up changes the inbox hundreds of times: the lists follow every few seconds meanwhile.
        _ = Task.Delay(_engine.State == SlackBotState.CatchingUp ? 3000 : 300).ContinueWith(_ => _ui.Post(() =>
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

        var before = ToDo.SelectMany(i => i.Ids).ToHashSet();
        Sync(ToDo, Cards(items.Where(i => i.IsToDo), url));
        var lastReport = _engine.Inbox.LastDigest;
        var knowing = items.Where(i => i.IsWorthKnowing).ToList();
        SyncGroups(knowing
            .GroupBy(i => i.Where)
            .OrderByDescending(g => g.Max(i => i.Time))
            .Select(g => (g.Key, g.Select(View).ToList()))
            .ToList());
        var since = DateTimeOffset.UtcNow - HandledShown;
        Sync(Handled, items
            .Where(i => i.Handled != HandledBy.None && i.HandledAt > since && i.Level is InboxLevel.Act or InboxLevel.Pending)
            .OrderByDescending(i => i.HandledAt)
            .Select(View)
            .ToList());

        ToDoCount = ToDo.Count;
        // Worth knowing shows on screen at the report times only: what came before the last one.
        ReportCount = lastReport is { } r ? knowing.Count(i => i.Time <= r) : 0;
        ReportTitle = lastReport is { } at
            ? $"{ReportCount} worth knowing ({at.ToLocalTime():HH:mm} report)"
            : "";
        HasToDo = ToDoCount > 0;
        HasReport = ReportCount > 0;
        HasOverlay = HasToDo || HasReport;
        Status = _engine.Status is { Length: > 0 } status ? status : _engine.State == SlackBotState.Off ? "Not connected (Slack Bot is off)." : "";
        SortProblem = _engine.SortProblem;
        HasSortProblem = SortProblem is not null;
        HasKnowing = Knowing.Count > 0;
        IsConnected = _engine.State == SlackBotState.Connected;
        // Not for what was already there when Helm started.
        // A card with a message not seen before: a new one, or one more in a thread already on the list.
        if (_shown && ToDo.Where(i => i.Ids.Any(id => !before.Contains(id))).Select(i => i.Alert).ToList() is { Count: > 0 } added)
        {
            NewToDo?.Invoke(this, EventArgs.Empty);
            NewToDoLines?.Invoke(this, added);
        }
        _shown = true;
    }

    /// <summary>
    /// The to-do messages as cards, one per thread (a DM's unthreaded messages are one thread, a channel message
    /// starts its own), in the order their first messages came: a card stays where it is as its thread grows.
    /// </summary>
    internal static List<InboxItemViewModel> Cards(IEnumerable<InboxItem> toDo, string url, DateTimeOffset? now = null) =>
        toDo.GroupBy(i => i.Kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage && i.ThreadTs is null
                ? i.Channel
                : i.Channel + ":" + (i.ThreadTs ?? i.Ts))
            .Select(g => g.OrderBy(i => i.Time).ToList())
            .Select(g => (Newest: g[^1], All: g))
            .Select(g => new InboxItemViewModel(g.Newest, SlackText.Permalink(url, g.Newest.Channel, g.Newest.Ts, g.Newest.ThreadTs), now, g.All))
            .ToList();

    /// <summary>
    /// Makes <paramref name="list"/> show <paramref name="target"/> by moving, inserting and removing rows, and
    /// replacing only those whose content changed: the rows on screen stay put.
    /// </summary>
    internal static void Sync(ObservableCollection<InboxItemViewModel> list, IReadOnlyList<InboxItemViewModel> target)
    {
        var wanted = target.Select(t => t.Id).ToHashSet();
        for (var i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(list[i].Id)) list.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i < list.Count && list[i].Id == t.Id)
            {
                if (list[i].Version != t.Version) list[i] = t;
                continue;
            }
            var at = -1;
            for (var j = i + 1; j < list.Count; j++)
                if (list[j].Id == t.Id) { at = j; break; }
            if (at < 0)
            {
                list.Insert(i, t);
                continue;
            }
            list.Move(at, i);
            if (list[i].Version != t.Version) list[i] = t;
        }
    }

    /// <summary>The worth-knowing groups, kept (with their unfolded state) while their messages change.</summary>
    private void SyncGroups(IReadOnlyList<(string Where, List<InboxItemViewModel> Items)> target)
    {
        var wanted = target.Select(t => t.Where).ToHashSet();
        for (var i = Knowing.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Knowing[i].Where)) Knowing.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            var (where, items) = target[i];
            var at = -1;
            for (var j = i; j < Knowing.Count; j++)
                if (Knowing[j].Where == where) { at = j; break; }
            InboxGroupViewModel group;
            if (at < 0)
            {
                group = new InboxGroupViewModel(where);
                Knowing.Insert(i, group);
            }
            else
            {
                if (at != i) Knowing.Move(at, i);
                group = Knowing[i];
            }
            Sync(group.Items, items);
        }
    }
}
