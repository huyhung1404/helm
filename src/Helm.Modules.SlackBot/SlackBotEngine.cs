using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.SlackBot;

public enum SlackBotState
{
    Off,
    /// <summary>No tokens yet (settings page → Connect).</summary>
    NoTokens,
    Connecting,
    /// <summary>Reading what came while Helm was not connected.</summary>
    CatchingUp,
    Connected,
    /// <summary>No network or Slack is down: trying again by itself.</summary>
    Offline,
    /// <summary>Slack refused the tokens: they have to be replaced.</summary>
    Failed,
}

/// <summary>
/// Runs SlackBot while the tool is on: signs in with the saved tokens, lists the user's conversations, keeps the
/// Socket Mode connection, catches up on what came while Helm was off (DMs, mentions, the user's threads, channels),
/// sorts messages with Claude (for the user: within seconds; the rest every few minutes), and opens the report at the
/// times set. Everything runs on worker threads and never throws; <see cref="Changed"/> says the state moved.
/// </summary>
public sealed class SlackBotEngine : IAsyncDisposable
{
    /// <summary>Messages for the user wait this long, so a burst is sorted in one run.</summary>
    internal static readonly TimeSpan ForYouDelay = TimeSpan.FromSeconds(15);

    /// <summary>Catching up never goes further back than this.</summary>
    internal static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(3);

    /// <summary>A conversation with a message this recent is read first when catching up.</summary>
    internal static readonly TimeSpan ActiveFor = TimeSpan.FromDays(30);

    /// <summary>The very first time, DMs and mentions of this long are read (channels start from now).</summary>
    internal static readonly TimeSpan FirstCatchUp = TimeSpan.FromHours(24);

    /// <summary>How often the safety net reads again what is for the user, and how far back.</summary>
    internal static readonly TimeSpan SafetyEvery = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan SafetyWindow = TimeSpan.FromMinutes(20);

    private readonly SlackTokens _tokens;
    private readonly SlackSorter _sorter;
    private readonly ISlackAgent _agent;
    private readonly ISettingsStore<SlackBotSettings> _settings;
    private readonly ILogger<SlackBotEngine> _logger;
    private readonly SemaphoreSlim _sortGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private SlackReader? _reader;
    private int _forYouPending;
    private bool _caughtUp;

    public SlackBotEngine(SlackTokens tokens, SlackInboxFile inbox, SlackSorter sorter, ISlackAgent agent, ISettingsStoreFactory settings,
        ILogger<SlackBotEngine> logger)
    {
        _agent = agent;
        _tokens = tokens;
        Inbox = inbox.Inbox;
        _sorter = sorter;
        _settings = settings.Get<SlackBotSettings>(SlackBotModule.ModuleId);
        _logger = logger;
    }

    public SlackInbox Inbox { get; }

    public SlackBotState State { get; private set; } = SlackBotState.Off;

    /// <summary>One line about the connection or the last problem, for the pages.</summary>
    public string Status { get; private set; } = "";

    /// <summary>A note about sorting (Claude Code missing or failing), or null when it works.</summary>
    public string? SortProblem { get; private set; }

    /// <summary>Who is signed in, once connected.</summary>
    public SlackIdentity? Me { get; private set; }

    /// <summary>Slack's API while connected, for the MCP tools (null otherwise).</summary>
    public ISlackApi? Api { get; private set; }

    /// <summary>The reader while connected: modes and conversation names, for the MCP tools.</summary>
    public SlackReader? Reader => _reader;

    /// <summary>The user's display name, once connected (for Claude and the reply template).</summary>
    public string MeName { get; private set; } = "";

    /// <summary>The user's conversations, once listed (names filled in for DMs).</summary>
    public IReadOnlyList<SlackConversation> Conversations { get; private set; } = [];

    /// <summary>Raised (on a worker thread) when the state, the status or the conversations changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised (on a worker thread) after Lumi replied for the user: who, where, Claude's line, the link.</summary>
    public event EventHandler<SlackAlertLine>? Replied;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _run = Task.Run(() => RunAsync(ct));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task run;
        lock (_gate)
        {
            cts = _cts;
            run = _run;
            _cts = null;
        }
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SlackBot stopped");
        }
        cts.Dispose();
        _reader = null;
        Api = null;
        Set(SlackBotState.Off, "");
    }

    /// <summary>Tests: as if connected with <paramref name="api"/>, without a socket.</summary>
    internal void ConnectedForTests(ISlackApi api, SlackIdentity me, string meName, IReadOnlyList<SlackConversation> conversations)
    {
        Me = me;
        MeName = meName;
        Conversations = conversations;
        _reader = new SlackReader(api, me, Inbox, () => _settings.Current);
        _reader.SetConversations(conversations);
        Api = api;
    }

    /// <summary>Tests: as if Lumi had just replied.</summary>
    internal void RaiseRepliedForTests(SlackAlertLine line) => Replied?.Invoke(this, line);

    /// <summary>New tokens, or Reconnect: starts over.</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    /// <summary>Sorts every waiting message now (the content page's Sort now).</summary>
    public Task SortNowAsync() => SortAsync(directOnly: false, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sortGate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_tokens.Load() is not { } tokens)
        {
            Set(SlackBotState.NoTokens, _tokens.Exist
                ? "The saved tokens cannot be read on this PC; connect again."
                : "Connect your Slack app on the settings page.");
            return;
        }
        using var api = new SlackApi(tokens.User, tokens.App);
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Set(SlackBotState.Connecting, "Connecting to Slack…");
                Me = await api.AuthTestAsync(ct).ConfigureAwait(false);
                Inbox.WorkspaceUrl = Me.Url;
                MeName = await api.UserNameAsync(Me.UserId, ct).ConfigureAwait(false) ?? Me.UserName;
                _logger.LogInformation("Slack Bot signed in to {Workspace}", Me.Url);
                var reader = new SlackReader(api, Me, Inbox, () => _settings.Current);
                reader.ForYou += (_, _) => SortSoon(ct);
                var replier = new SlackReplier(api, _agent, Inbox, reader, () => _settings.Current, MeName, _logger,
                    item => Replied?.Invoke(this, new SlackAlertLine(item.From, item.Where, item.Summary,
                        SlackText.Permalink(Me?.Url ?? "https://slack.com/", item.Channel, item.Ts, item.ThreadTs))));
                await RefreshConversationsAsync(api, reader, ct).ConfigureAwait(false);
                _reader = reader;
                Api = api;
                var socket = new SlackSocket(api);
                socket.ConnectedChanged += (_, connected) =>
                {
                    if (!connected) _caughtUp = false;
                    if (!connected && !ct.IsCancellationRequested) Set(SlackBotState.Offline, "Lost the connection to Slack; trying again…");
                };
                using var timers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var background = TimersAsync(api, reader, replier, timers.Token);
                try
                {
                    await socket.RunAsync(
                        e => OnEventAsync(reader, e, ct),
                        () => CatchUpAsync(api, reader, ct),
                        ex => _logger.LogInformation(ex, "SlackBot's connection dropped"),
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    await timers.CancelAsync().ConfigureAwait(false);
                    await background.ConfigureAwait(false);
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SlackApiException ex) when (ex.IsAuthProblem)
            {
                _logger.LogWarning(ex, "Slack refused SlackBot's tokens");
                Set(SlackBotState.Failed, ex.Error == "missing_scope"
                    ? "The Slack app lacks a permission; update it from the manifest on the settings page and reinstall it."
                    : $"Slack refused the tokens ({ex.Error}); connect again with new ones.");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or SlackApiException or TaskCanceledException or IOException)
            {
                _logger.LogInformation(ex, "SlackBot could not reach Slack");
                Set(SlackBotState.Offline, "Cannot reach Slack; trying again…");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SlackBot failed");
                Set(SlackBotState.Offline, "Something went wrong: " + ex.Message);
            }
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
        }
    }

    private async Task OnEventAsync(SlackReader reader, System.Text.Json.JsonElement json, CancellationToken ct)
    {
        try
        {
            if (SlackEventParser.Parse(json) is { } e) await reader.HandleAsync(e, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SlackBot could not take in an event");
        }
    }

    /// <summary>
    /// Reads what came since the last time SlackBot was connected: DMs and group DMs, channels, replies in the user's
    /// threads, and mentions anywhere (search finds those in other threads). Only when all of it is in does "seen" move
    /// on, so a failure half-way is read again next time.
    /// </summary>
    private async Task CatchUpAsync(ISlackApi api, SlackReader reader, CancellationToken ct)
    {
        try
        {
            var started = DateTimeOffset.UtcNow;
            var first = Inbox.LastSeen is null;
            var floor = SlackIds.Ts(started - (first ? FirstCatchUp : MaxCatchUp));
            var since = first || SlackIds.Compare(Inbox.LastSeen, floor) < 0 ? floor : Inbox.LastSeen!;
            // Only what the user turned on, and never DMs with people who left; the first time, channels start from now:
            // a day of every channel would bury what matters.
            var conversations = Conversations.Where(c => !c.Gone && reader.Reads(c.Id) && (!first || c.IsDirect)).ToList();
            // Active ones first (a workspace keeps hundreds of old DMs): once they are in, Slack Bot is ready and the
            // rest is read in the background. "Seen" moves only when all of it is in.
            var recent = Inbox.RecentChannels(ActiveFor);
            var active = conversations.Where(c => recent.Contains(c.Id)).ToList();
            var older = conversations.Where(c => !recent.Contains(c.Id)).ToList();
            _logger.LogInformation("Slack Bot catching up on {Active} active and {Older} older conversations since {Since}",
                active.Count, older.Count, SlackIds.Time(since));
            var done = 0;
            foreach (var c in active)
            {
                Set(SlackBotState.CatchingUp, $"Catching up: {done++} of {active.Count} active conversations…");
                await ReadHistoryAsync(api, reader, c, since, ct).ConfigureAwait(false);
            }
            await ReadForYouAsync(api, reader, since, ct).ConfigureAwait(false);
            // Quiet DMs (nobody wrote for a month) are left out once Slack Bot knows which they are, unless the user
            // wants them; the first time each one's latest message is looked up (one call), and read only if it is new.
            var learned = Inbox.ActivityLearned;
            if (learned && !_settings.Current.CatchUpQuietDms) older.RemoveAll(c => c.IsDirect);
            Set(SlackBotState.Connected, older.Count > 0 ? Connected() + $" Checking {older.Count} quieter conversations in the background." : Connected());
            SortSoon(ct);
            done = 0;
            foreach (var c in older)
            {
                if (++done % 10 == 0) Set(SlackBotState.Connected, Connected() + $" Checking quieter conversations: {done} of {older.Count}.");
                if (c.IsDirect && !learned)
                {
                    if (await LatestAsync(api, c, ct).ConfigureAwait(false) is not { } latest) continue;
                    Inbox.Touch(c.Id, latest);
                    if (SlackIds.Compare(latest, since) <= 0) continue;
                }
                await ReadHistoryAsync(api, reader, c, since, ct).ConfigureAwait(false);
            }
            if (!learned && conversations.Any(c => c.IsDirect)) Inbox.ActivityLearned = true;
            Inbox.Seen(SlackIds.Ts(started));
            _caughtUp = true;
            _logger.LogInformation("Slack Bot caught up in {Seconds:0} s", (DateTimeOffset.UtcNow - started).TotalSeconds);
            Set(SlackBotState.Connected, Connected());
            SortSoon(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The socket stays; the next connection catches up from the same point.
            _logger.LogWarning(ex, "SlackBot could not catch up");
            Set(SlackBotState.Connected, Connected() + " Catching up failed; it runs again on the next connection.");
        }
    }

    /// <summary>The ts of a conversation's latest message, or null when it has none or cannot be read.</summary>
    private static async Task<string?> LatestAsync(ISlackApi api, SlackConversation c, CancellationToken ct)
    {
        try
        {
            return (await api.HistoryAsync(c.Id, "0", 1, ct).ConfigureAwait(false)).LastOrDefault()?.Ts;
        }
        catch (SlackApiException ex) when (!ex.IsAuthProblem)
        {
            return null;
        }
    }

    /// <summary>One conversation's messages since <paramref name="since"/>; one that went away meanwhile is skipped.</summary>
    private static async Task ReadHistoryAsync(ISlackApi api, SlackReader reader, SlackConversation c, string since, CancellationToken ct)
    {
        try
        {
            foreach (var m in await api.HistoryAsync(c.Id, since, 200, ct).ConfigureAwait(false))
                await reader.ReadAsync(m, ct).ConfigureAwait(false);
        }
        catch (SlackApiException ex) when (!ex.IsAuthProblem)
        {
            // Archived, left, or a DM closed while catching up.
        }
    }

    /// <summary>Replies in the user's threads and mentions anywhere since <paramref name="since"/>.</summary>
    private async Task ReadForYouAsync(ISlackApi api, SlackReader reader, string since, CancellationToken ct)
    {
        foreach (var (channel, thread) in Inbox.MyThreads(TimeSpan.FromDays(7)))
        {
            if (!reader.Reads(channel)) continue;
            try
            {
                foreach (var m in await api.RepliesAsync(channel, thread, since, ct).ConfigureAwait(false))
                    await reader.ReadAsync(m, ct).ConfigureAwait(false);
            }
            catch (SlackApiException ex) when (!ex.IsAuthProblem)
            {
                // A thread deleted, or a channel left.
            }
        }
        try
        {
            foreach (var m in await api.SearchMentionsAsync(reader.Me.UserId, since, ct).ConfigureAwait(false))
                await reader.ReadAsync(m, ct).ConfigureAwait(false);
        }
        catch (SlackApiException ex) when (!ex.IsAuthProblem || ex.Error == "missing_scope")
        {
            _logger.LogInformation(ex, "SlackBot could not search for mentions");
        }
    }

    /// <summary>
    /// The safety net beside the live connection: what is for the user of the last <see cref="SafetyWindow"/> is read
    /// again — the DMs and group DMs that were active lately (a workspace keeps hundreds of old ones), their threads
    /// and mentions anywhere. Anything the socket already brought is skipped; anything it missed lands on the list
    /// within <see cref="SafetyEvery"/>.
    /// </summary>
    private async Task CheckForYouAsync(ISlackApi api, SlackReader reader, CancellationToken ct)
    {
        var since = SlackIds.Ts(DateTimeOffset.UtcNow - SafetyWindow);
        var active = Inbox.ActiveChannels();
        foreach (var c in Conversations.Where(c => c.IsDirect && !c.Gone && active.Contains(c.Id) && reader.Reads(c.Id)))
        {
            try
            {
                foreach (var m in await api.HistoryAsync(c.Id, since, 100, ct).ConfigureAwait(false))
                    await reader.ReadAsync(m, ct).ConfigureAwait(false);
            }
            catch (SlackApiException ex) when (!ex.IsAuthProblem)
            {
                // A DM closed meanwhile.
            }
        }
        await ReadForYouAsync(api, reader, since, ct).ConfigureAwait(false);
    }

    /// <summary>Channel sorting, report times, "seen", the conversation list and forgetting old messages.</summary>
    private async Task TimersAsync(ISlackApi api, SlackReader reader, SlackReplier replier, CancellationToken ct)
    {
        var lastChannelSort = DateTimeOffset.UtcNow;
        var lastList = DateTimeOffset.UtcNow;
        var lastCheck = DateTimeOffset.UtcNow;
        Inbox.Prune();
        // Every few seconds: channel sorting may be set in seconds; each tick is cheap when nothing is due.
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await tick.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_caughtUp) Inbox.Seen(SlackIds.Ts(now - TimeSpan.FromMinutes(1)));
                    if (ReportDue(_settings.Current.ReportTimes, Inbox.LastDigest, DateTimeOffset.Now) is { } due) Inbox.Digest(due);
                    if (now - lastChannelSort >= TimeSpan.FromSeconds(Math.Max(SlackBotSettings.MinChannelSeconds, _settings.Current.ChannelSeconds)))
                    {
                        lastChannelSort = now;
                        await SortAsync(directOnly: false, ct).ConfigureAwait(false);
                    }
                    if (_caughtUp) await replier.ReplyAsync(now, ct).ConfigureAwait(false);
                    if (_caughtUp && now - lastCheck >= SafetyEvery)
                    {
                        lastCheck = now;
                        await CheckForYouAsync(api, reader, ct).ConfigureAwait(false);
                    }
                    if (now - lastList >= TimeSpan.FromHours(1))
                    {
                        lastList = now;
                        Inbox.Prune();
                        await RefreshConversationsAsync(api, reader, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogInformation(ex, "SlackBot's timer failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The latest report time that passed since the last report, or null when none did.</summary>
    internal static DateTimeOffset? ReportDue(IEnumerable<string> times, DateTimeOffset? last, DateTimeOffset now)
    {
        DateTimeOffset? due = null;
        foreach (var text in times)
        {
            if (!TimeOnly.TryParseExact(text.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var today = new DateTimeOffset(now.Date + time.ToTimeSpan(), now.Offset);
            var at = today <= now ? today : today.AddDays(-1);
            if (due is null || at > due) due = at;
        }
        if (due is null) return null;
        if (last is null) return due;
        return due > last ? due : null;
    }

    private async Task RefreshConversationsAsync(ISlackApi api, SlackReader reader, CancellationToken ct)
    {
        var list = new List<SlackConversation>();
        foreach (var c in await api.ConversationsAsync(ct).ConfigureAwait(false))
        {
            list.Add(c.Kind == SlackConversationKind.DirectMessage && c.UserId is { } user
                ? c with { Name = await api.UserNameAsync(user, ct).ConfigureAwait(false) ?? user }
                : c);
        }
        list.Sort((a, b) => (a.IsDirect, a.Name).CompareTo((b.IsDirect, b.Name)));
        reader.SetConversations(list);
        // Turned off (or off by default): nothing from it is kept.
        foreach (var c in list.Where(c => !reader.Reads(c.Id))) Inbox.Forget(c.Id);
        Conversations = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A message for the user came: sort within <see cref="ForYouDelay"/> (one run for a burst).</summary>
    private void SortSoon(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _forYouPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ForYouDelay, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _forYouPending, 0);
                await SortAsync(directOnly: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _forYouPending, 0);
            }
        }, CancellationToken.None);
    }

    private async Task SortAsync(bool directOnly, CancellationToken ct)
    {
        if (Me is not { } me) return;
        if (_sorter.Unavailable() is { } problem)
        {
            SetSortProblem(problem + " Messages for you show unsorted until it is.");
            return;
        }
        await _sortGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A few runs at most: the rest waits for the next turn.
            for (var run = 0; run < 5; run++)
            {
                var sorted = await _sorter.SortAsync(Inbox, directOnly, MeName is { Length: > 0 } name ? name : me.UserName, _settings.Current, ct).ConfigureAwait(false);
                if (sorted < SlackSorter.BatchSize) break;
            }
            SetSortProblem(null);
        }
        catch (ClaudeCodeException ex)
        {
            _logger.LogWarning(ex, "SlackBot could not sort messages");
            SetSortProblem("Claude Code could not sort the messages: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SlackBot's sorting failed");
            SetSortProblem("Sorting failed: " + ex.Message);
        }
        finally
        {
            _sortGate.Release();
        }
    }

    private string Connected() => Me is { } me ? $"Connected as {me.UserName}." : "Connected.";

    private void SetSortProblem(string? problem)
    {
        if (SortProblem == problem) return;
        SortProblem = problem;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Set(SlackBotState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
