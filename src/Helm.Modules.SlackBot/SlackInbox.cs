namespace Helm.Modules.SlackBot;

/// <summary>
/// The messages SlackBot keeps and what became of them. An item the user has to deal with stays on the to-do list —
/// the overlay — until the user replies in that DM or thread, reacts with an emoji, or presses Done; never because
/// Claude or anyone else answered. Thread-safe; <see cref="Changed"/> is raised (on the caller's thread) after every
/// change, and the data is written to disk shortly after (<see cref="SlackInboxFile"/>).
/// </summary>
public sealed class SlackInbox
{
    /// <summary>How long messages are kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(14);

    /// <summary>Runs that may skip an item before it is shown unsorted.</summary>
    public const int MaxSortTries = 3;

    /// <summary>Corrections shown to Claude (the latest).</summary>
    public const int MaxCorrections = 20;

    private readonly object _gate = new();
    private readonly InboxData _data;
    private readonly Dictionary<string, InboxItem> _byId;
    private readonly TimeProvider _clock;
    private readonly Action<InboxData>? _save;
    private readonly HashSet<string> _sending = [];

    /// <param name="save">Called under the lock after each change (debounced by the caller), or null in tests.</param>
    public SlackInbox(InboxData data, TimeProvider clock, Action<InboxData>? save = null)
    {
        _data = data;
        _clock = clock;
        _save = save;
        _byId = data.Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public event EventHandler? Changed;

    public static string ScopeOf(string channel, SlackConversationKind kind, string? threadTs) =>
        kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage ? channel
        : threadTs is { } t ? channel + ":" + t
        : "root:" + channel;

    public string? LastSeen
    {
        get
        {
            lock (_gate) return _data.LastSeen;
        }
    }

    /// <summary>The workspace's address, kept from the last connection.</summary>
    public string? WorkspaceUrl
    {
        get
        {
            lock (_gate) return _data.WorkspaceUrl;
        }
        set
        {
            lock (_gate)
            {
                if (_data.WorkspaceUrl == value) return;
                _data.WorkspaceUrl = value;
                _save?.Invoke(_data);
            }
        }
    }

    public DateTimeOffset? LastDigest
    {
        get
        {
            lock (_gate) return _data.LastDigest;
        }
    }

    /// <summary>A message was seen in <paramref name="channel"/> (anyone's, kept or not).</summary>
    public void Touch(string channel, string ts)
    {
        lock (_gate)
        {
            if (_data.Activity.TryGetValue(channel, out var last) && SlackIds.Compare(ts, last) <= 0) return;
            _data.Activity[channel] = ts;
            _save?.Invoke(_data);
        }
    }

    /// <summary>Every DM's latest message was looked up once (so quiet DMs can be left out of catching up).</summary>
    public bool ActivityLearned
    {
        get
        {
            lock (_gate) return _data.ActivityLearned;
        }
        set
        {
            lock (_gate)
            {
                _data.ActivityLearned = value;
                _save?.Invoke(_data);
            }
        }
    }

    /// <summary>Conversations with a message seen in the last <paramref name="within"/>.</summary>
    public IReadOnlySet<string> RecentChannels(TimeSpan within)
    {
        var since = SlackIds.Ts(_clock.GetUtcNow() - within);
        lock (_gate) return _data.Activity.Where(p => SlackIds.Compare(p.Value, since) > 0).Select(p => p.Key).ToHashSet();
    }

    /// <summary>The message is kept.</summary>
    public bool Contains(string channel, string ts)
    {
        lock (_gate) return _byId.ContainsKey(SlackIds.Message(channel, ts));
    }

    /// <summary>The user has written in this DM or thread (lately).</summary>
    public bool WroteIn(string scope)
    {
        lock (_gate) return _data.LastMine.ContainsKey(scope);
    }

    /// <summary>The words of a kept message (a thread's start, for context), clipped; null when not kept.</summary>
    public string? TextOf(string channel, string ts)
    {
        lock (_gate) return _byId.TryGetValue(SlackIds.Message(channel, ts), out var item) ? SlackText.Clip(item.Text, 300) : null;
    }

    /// <summary>Remembers that everything up to <paramref name="ts"/> was seen (only ever moves forward).</summary>
    public void Seen(string ts)
    {
        lock (_gate)
        {
            if (SlackIds.Compare(ts, _data.LastSeen) <= 0) return;
            _data.LastSeen = ts;
            _save?.Invoke(_data);
        }
    }

    /// <summary>Adds a message from someone else; false when it is already there. Answered already (the user wrote after it): kept as handled.</summary>
    public bool Add(InboxItem item)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(item.Id)) return false;
            if (_data.LastMine.TryGetValue(item.Scope, out var mine) && SlackIds.Compare(item.Ts, mine) < 0
                && (item.ThreadTs is not null || item.Kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage || item.Direct))
            {
                item.Handled = HandledBy.Replied;
                item.HandledAt = _clock.GetUtcNow();
            }
            _data.Items.Add(item);
            _byId[item.Id] = item;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// The user wrote <paramref name="message"/>: what it answers is handled. In a DM or group DM that is everything
    /// before it; in a thread, the thread's earlier messages; outside threads, the earlier messages that called them.
    /// </summary>
    public void Mine(SlackMessage message, SlackConversationKind kind)
    {
        var scope = ScopeOf(message.Channel, kind, message.ThreadTs);
        lock (_gate)
        {
            if (!_data.LastMine.TryGetValue(scope, out var last) || SlackIds.Compare(message.Ts, last) > 0) _data.LastMine[scope] = message.Ts;
            foreach (var item in _data.Items)
            {
                if (item.Handled != HandledBy.None || item.Scope != scope || SlackIds.Compare(item.Ts, message.Ts) >= 0) continue;
                // Writing in a channel answers only those who called the user there.
                if (scope.StartsWith("root:", StringComparison.Ordinal) && !item.Direct) continue;
                Handle(item, HandledBy.Replied);
            }
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The user reacted to a message: it is handled, and in a DM or thread so is everything before it there (a 👍 on
    /// the last message answers the conversation).
    /// </summary>
    public void Reacted(string channel, string ts)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(SlackIds.Message(channel, ts), out var target))
            {
                var scope = target.Scope;
                var wholeScope = !scope.StartsWith("root:", StringComparison.Ordinal);
                foreach (var item in _data.Items)
                {
                    if (item.Handled != HandledBy.None) continue;
                    if (item == target || (wholeScope && item.Scope == scope && SlackIds.Compare(item.Ts, ts) < 0)) Handle(item, HandledBy.Reacted);
                }
            }
            else
            {
                // A message SlackBot did not keep (one of the user's own, or skipped): in a DM it still answers what came before.
                foreach (var item in _data.Items)
                {
                    if (item.Handled == HandledBy.None && item.Channel == channel && item.Scope == channel && SlackIds.Compare(item.Ts, ts) < 0)
                        Handle(item, HandledBy.Reacted);
                }
            }
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Edited(string channel, string ts, string text)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(SlackIds.Message(channel, ts), out var item)) return;
            item.Text = text;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Deleted(string channel, string ts)
    {
        lock (_gate)
        {
            if (!_byId.Remove(SlackIds.Message(channel, ts), out var item)) return;
            _data.Items.Remove(item);
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A channel the user stopped reading: everything kept from it goes, except the messages that tag them.</summary>
    public void Forget(string channel)
    {
        lock (_gate)
        {
            if (_data.Items.RemoveAll(i => i.Channel == channel && !i.Tagged) == 0) return;
            _byId.Clear();
            foreach (var item in _data.Items) _byId[item.Id] = item;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unsorted messages, oldest first: those for the user only, or all of them.</summary>
    public IReadOnlyList<InboxItem> ToSort(bool directOnly, int max)
    {
        lock (_gate)
        {
            return _data.Items
                .Where(i => i.Level == InboxLevel.Pending && i.Handled == HandledBy.None && (!directOnly || i.Direct))
                .OrderBy(i => i.Ts, Comparer<string>.Create(SlackIds.Compare))
                .Take(max)
                .Select(Copy)
                .ToList();
        }
    }

    /// <summary>
    /// Claude's levels for <paramref name="sent"/>. An item it skipped waits for the next run; after
    /// <see cref="MaxSortTries"/> it is shown as it is (to do when it is for the user, worth knowing otherwise).
    /// </summary>
    public void Sorted(IReadOnlyList<InboxItem> sent, IReadOnlyDictionary<string, (InboxLevel Level, string? Summary)> levels)
    {
        lock (_gate)
        {
            foreach (var copy in sent)
            {
                if (!_byId.TryGetValue(copy.Id, out var item) || item.Level != InboxLevel.Pending) continue;
                if (levels.TryGetValue(item.Id, out var sorted) && sorted.Level != InboxLevel.Pending)
                {
                    // Claude never takes a message for the user off the screen: at worst it is to do.
                    item.Level = item.Direct && sorted.Level == InboxLevel.Skip ? InboxLevel.Know : sorted.Level;
                    item.Summary = sorted.Summary;
                }
                else if (++item.SortTries >= MaxSortTries)
                {
                    item.Level = item.Direct ? InboxLevel.Act : InboxLevel.Know;
                }
            }
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Messages for the user that are still to do, sorted, at least <paramref name="after"/> old (the user had time to
    /// answer) and at most <paramref name="maxAge"/> (no reply hours later), with no reply from Slack Bot in their DM or
    /// thread for <paramref name="perScope"/>: the latest one per DM or thread.
    /// </summary>
    public IReadOnlyList<InboxItem> ToReply(DateTimeOffset now, TimeSpan after, TimeSpan maxAge, TimeSpan perScope)
    {
        lock (_gate)
        {
            return _data.Items
                .Where(i => i.IsToDo && i.Level == InboxLevel.Act && i.Direct && i.BotReplyAt is null
                            && i.Time <= now - after && i.Time >= now - maxAge
                            && !(_data.BotScopes.TryGetValue(i.Scope, out var last) && last > now - perScope))
                .GroupBy(i => i.Scope)
                .Select(g => Copy(g.OrderBy(i => i.Ts, Comparer<string>.Create(SlackIds.Compare)).Last()))
                .ToList();
        }
    }

    /// <summary>A reply is on its way to <paramref name="channel"/>: the user's account writing there now is Slack Bot.</summary>
    public void Sending(string channel)
    {
        lock (_gate) _sending.Add(channel);
    }

    /// <summary>The reply could not be sent.</summary>
    public void NotSent(string channel)
    {
        lock (_gate) _sending.Remove(channel);
    }

    /// <summary>
    /// Slack Bot replied to <paramref name="id"/> with <paramref name="text"/>, or decided not to (null text): it is
    /// not asked again for that message. The item stays to do.
    /// </summary>
    public void BotReplied(string id, string channel, string? ts, string? text)
    {
        lock (_gate)
        {
            _sending.Remove(channel);
            if (ts is not null)
            {
                _data.BotMessages.Add(SlackIds.Message(channel, ts));
                if (_data.BotMessages.Count > 500) _data.BotMessages.RemoveRange(0, _data.BotMessages.Count - 500);
            }
            if (_byId.TryGetValue(id, out var item))
            {
                item.BotReply = text;
                item.BotReplyAt = _clock.GetUtcNow();
                _data.BotScopes[item.Scope] = _clock.GetUtcNow();
            }
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An AI agent posted <paramref name="ts"/> through Slack Bot (a report): the user's account, not the user's answer.</summary>
    public void BotPosted(string channel, string? ts)
    {
        lock (_gate)
        {
            _sending.Remove(channel);
            if (ts is null) return;
            _data.BotMessages.Add(SlackIds.Message(channel, ts));
            _save?.Invoke(_data);
        }
    }

    /// <summary>A message of the user's account that Slack Bot wrote (sent, or being sent to that channel).</summary>
    public bool IsBotMessage(string channel, string ts)
    {
        lock (_gate) return _sending.Contains(channel) || _data.BotMessages.Contains(SlackIds.Message(channel, ts));
    }

    /// <summary>Done: off the to-do list.</summary>
    public void Dismiss(string id) => Update(id, item => Handle(item, HandledBy.Dismissed));

    /// <summary>Back on the to-do list.</summary>
    public void Restore(string id) => Update(id, item =>
    {
        item.Handled = HandledBy.None;
        item.HandledAt = null;
        if (item.Level is InboxLevel.Know or InboxLevel.Skip) item.Level = InboxLevel.Act;
    });

    /// <summary>The user says Claude chose the wrong level; Claude sees it next time.</summary>
    public void Relevel(string id, InboxLevel level) => Update(id, item =>
    {
        item.Level = level;
        item.Read = false;
        _data.Corrections.Add(new InboxCorrection(item.Where, SlackText.Clip(item.Text, 200), level));
        if (_data.Corrections.Count > MaxCorrections) _data.Corrections.RemoveRange(0, _data.Corrections.Count - MaxCorrections);
    });

    public void MarkRead(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            foreach (var item in _data.Items)
                if (set.Contains(item.Id)) item.Read = true;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A report time came: the worth-knowing messages since the last one show up on screen.</summary>
    public void Digest(DateTimeOffset at)
    {
        lock (_gate)
        {
            _data.LastDigest = at;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<InboxCorrection> Corrections()
    {
        lock (_gate) return [.. _data.Corrections];
    }

    /// <summary>A copy of every item (for the pages), oldest first.</summary>
    public IReadOnlyList<InboxItem> Snapshot()
    {
        lock (_gate) return _data.Items.OrderBy(i => i.Ts, Comparer<string>.Create(SlackIds.Compare)).Select(Copy).ToList();
    }

    /// <summary>Hands the data to <paramref name="write"/> under the lock (saving it).</summary>
    public T Export<T>(Func<InboxData, T> write)
    {
        lock (_gate) return write(_data);
    }

    /// <summary>Forgets what is older than <see cref="Keep"/>.</summary>
    public void Prune()
    {
        var oldest = SlackIds.Ts(_clock.GetUtcNow() - Keep);
        lock (_gate)
        {
            var gone = _data.Items.RemoveAll(i => SlackIds.Compare(i.Ts, oldest) < 0);
            foreach (var key in _data.LastMine.Where(p => SlackIds.Compare(p.Value, oldest) < 0).Select(p => p.Key).ToList()) _data.LastMine.Remove(key);
            var since = _clock.GetUtcNow() - Keep;
            foreach (var key in _data.BotScopes.Where(p => p.Value < since).Select(p => p.Key).ToList()) _data.BotScopes.Remove(key);
            if (gone == 0) return;
            _byId.Clear();
            foreach (var item in _data.Items) _byId[item.Id] = item;
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Conversations with anything kept or written by the user in them (the last <see cref="Keep"/>).</summary>
    public IReadOnlySet<string> ActiveChannels()
    {
        lock (_gate)
        {
            var set = _data.Items.Select(i => i.Channel).ToHashSet();
            var since = SlackIds.Ts(_clock.GetUtcNow() - Keep);
            foreach (var (channel, ts) in _data.Activity)
                if (SlackIds.Compare(ts, since) > 0) set.Add(channel);
            foreach (var scope in _data.LastMine.Keys)
                set.Add(scope.StartsWith("root:", StringComparison.Ordinal) ? scope[5..] : scope.Split(':')[0]);
            return set;
        }
    }

    /// <summary>Threads the user wrote in lately (channel, thread ts): checked for replies missed while offline.</summary>
    public IReadOnlyList<(string Channel, string ThreadTs)> MyThreads(TimeSpan within)
    {
        var since = SlackIds.Ts(_clock.GetUtcNow() - within);
        lock (_gate)
        {
            return _data.LastMine
                .Where(p => !p.Key.StartsWith("root:", StringComparison.Ordinal) && p.Key.Contains(':') && SlackIds.Compare(p.Value, since) > 0)
                .Select(p => p.Key.Split(':', 2))
                .Select(p => (p[0], p[1]))
                .ToList();
        }
    }

    private void Update(string id, Action<InboxItem> change)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var item)) return;
            change(item);
            _save?.Invoke(_data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Handle(InboxItem item, HandledBy how)
    {
        item.Handled = how;
        item.HandledAt = _clock.GetUtcNow();
    }

    private static InboxItem Copy(InboxItem i) => new()
    {
        Channel = i.Channel, Ts = i.Ts, ThreadTs = i.ThreadTs, Kind = i.Kind, Where = i.Where, From = i.From, Text = i.Text,
        ThreadStart = i.ThreadStart, Direct = i.Direct, Level = i.Level, Summary = i.Summary, SortTries = i.SortTries,
        Handled = i.Handled, HandledAt = i.HandledAt, Read = i.Read, BotReply = i.BotReply, BotReplyAt = i.BotReplyAt, Tagged = i.Tagged,
    };
}
