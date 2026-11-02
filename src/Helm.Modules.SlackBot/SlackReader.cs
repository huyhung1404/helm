using System.Text.RegularExpressions;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Turns what Slack sends into the inbox: other people's messages become items (with names, where they were written
/// and whether they are for the user), the user's own messages and reactions mark what they answer as handled.
/// Blocked conversations are dropped here, before anything is kept.
/// </summary>
public sealed partial class SlackReader(ISlackApi api, SlackIdentity me, SlackInbox inbox, Func<SlackBotSettings> settings)
{
    private Dictionary<string, SlackConversation> _conversations = [];

    /// <summary>Raised when a message for the user arrived (sort it soon).</summary>
    public event EventHandler? ForYou;

    public SlackIdentity Me => me;

    public void SetConversations(IEnumerable<SlackConversation> conversations) =>
        _conversations = conversations.ToDictionary(c => c.Id);

    public async Task HandleAsync(SlackEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case SlackMessagePosted posted:
                await ReadAsync(posted.Message, ct).ConfigureAwait(false);
                break;
            case SlackMessageEdited edited when !IsBlocked(edited.Channel):
                inbox.Edited(edited.Channel, edited.Ts, await PlainAsync(edited.Text, ct).ConfigureAwait(false));
                break;
            case SlackMessageDeleted deleted:
                inbox.Deleted(deleted.Channel, deleted.Ts);
                break;
            case SlackReactionAdded reaction when reaction.User == me.UserId:
                inbox.Reacted(reaction.Channel, reaction.Ts);
                break;
        }
    }

    /// <summary>Takes in one message (live or caught up); false when it was not kept.</summary>
    public async Task<bool> ReadAsync(SlackMessage m, CancellationToken ct)
    {
        if (IsBlocked(m.Channel)) return false;
        var conversation = Conversation(m.Channel);
        if (m.User == me.UserId)
        {
            inbox.Mine(m, conversation.Kind);
            return false;
        }
        var callsMe = SlackText.Mentions(m.Text, me.UserId);
        if (m.FromBot && !settings().ReadBots && !callsMe) return false;
        var scope = SlackInbox.ScopeOf(m.Channel, conversation.Kind, m.ThreadTs);
        var direct = conversation.IsDirect || callsMe || m.ParentUserId == me.UserId || (m.IsReply && inbox.WroteIn(scope));
        var item = new InboxItem
        {
            Channel = m.Channel,
            Ts = m.Ts,
            ThreadTs = m.ThreadTs,
            Kind = conversation.Kind,
            Where = Where(conversation, m.IsReply),
            From = m.User is { } user ? await api.UserNameAsync(user, ct).ConfigureAwait(false) ?? "Someone" : "A bot",
            Text = await PlainAsync(m.Text, ct).ConfigureAwait(false),
            ThreadStart = m.IsReply ? inbox.TextOf(m.Channel, m.ThreadTs!) : null,
            Direct = direct,
        };
        if (!inbox.Add(item)) return false;
        if (direct && item.Handled == HandledBy.None) ForYou?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal static string Where(SlackConversation c, bool reply) => c.Kind switch
    {
        SlackConversationKind.DirectMessage => reply ? "DM thread" : "DM",
        SlackConversationKind.GroupMessage => (reply ? "thread with " : "") + c.Name,
        _ => (reply ? "thread in #" : "#") + c.Name,
    };

    private bool IsBlocked(string channel) => settings().Blocked.Contains(channel);

    /// <summary>A conversation Slack has not listed yet (joined a moment ago): guessed from its id.</summary>
    private SlackConversation Conversation(string id) =>
        _conversations.TryGetValue(id, out var c) ? c
        : new SlackConversation(id, id, id.StartsWith('D') ? SlackConversationKind.DirectMessage : SlackConversationKind.Channel);

    /// <summary>The text with names put in for the people it calls.</summary>
    private async Task<string> PlainAsync(string text, CancellationToken ct)
    {
        var names = new Dictionary<string, string?>();
        foreach (Match match in UserTag().Matches(text))
        {
            var id = match.Groups[1].Value;
            if (!names.ContainsKey(id)) names[id] = await api.UserNameAsync(id, ct).ConfigureAwait(false);
        }
        return SlackText.Plain(text, id => names.GetValueOrDefault(id));
    }

    [GeneratedRegex("<@([A-Z0-9]+)")]
    private static partial Regex UserTag();
}
