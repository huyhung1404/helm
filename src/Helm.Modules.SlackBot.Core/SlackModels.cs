using System.Globalization;

namespace Helm.Modules.SlackBot;

public enum SlackConversationKind
{
    Channel,
    PrivateChannel,
    DirectMessage,
    GroupMessage,
}

/// <summary>A channel, a private channel, a DM or a group DM the user is in.</summary>
/// <param name="UserId">The other person of a DM.</param>
/// <param name="Gone">A DM with someone whose account was removed: nothing new can come from it.</param>
public sealed record SlackConversation(string Id, string Name, SlackConversationKind Kind, string? UserId = null, bool Gone = false)
{
    /// <summary>A DM or a group DM: everything in it is for the user.</summary>
    public bool IsDirect => Kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage;
}

/// <summary>Who the user is on Slack (auth.test).</summary>
/// <param name="Url">The workspace's address, e.g. https://ikame.slack.com/.</param>
public sealed record SlackIdentity(string UserId, string UserName, string TeamId, string Url);

/// <summary>One message as Slack sends it (text still in Slack's markup, see <see cref="SlackText"/>).</summary>
/// <param name="ThreadTs">The thread it is in: its parent's ts (the parent itself has its own ts here), or null.</param>
/// <param name="ParentUserId">Who started the thread it replies in, when Slack says.</param>
public sealed record SlackMessage(string Channel, string Ts, string? ThreadTs, string? User, string Text, bool FromBot,
    string? ParentUserId = null)
{
    public string Id => SlackIds.Message(Channel, Ts);

    /// <summary>A reply inside a thread (not the thread's first message).</summary>
    public bool IsReply => ThreadTs is { } t && t != Ts;
}

/// <summary>What Slack told Helm (Socket Mode), in the few kinds SlackBot uses.</summary>
public abstract record SlackEvent;

public sealed record SlackMessagePosted(SlackMessage Message) : SlackEvent;

public sealed record SlackMessageEdited(string Channel, string Ts, string Text) : SlackEvent;

public sealed record SlackMessageDeleted(string Channel, string Ts) : SlackEvent;

public sealed record SlackReactionAdded(string User, string Channel, string Ts) : SlackEvent;

public static class SlackIds
{
    public static string Message(string channel, string ts) => channel + ":" + ts;

    /// <summary>Slack's message id ("1730512345.001200") is also its time, in seconds since 1970.</summary>
    public static DateTimeOffset Time(string ts) =>
        double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : DateTimeOffset.MinValue;

    /// <summary>The ts of a moment, for Slack's "oldest" arguments.</summary>
    public static string Ts(DateTimeOffset time) =>
        (time.ToUnixTimeMilliseconds() / 1000.0).ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>Compares two ts the way Slack orders messages.</summary>
    public static int Compare(string? a, string? b)
    {
        if (a == b) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var (aSec, aSub) = Split(a);
        var (bSec, bSub) = Split(b);
        var c = aSec.CompareTo(bSec);
        return c != 0 ? c : aSub.CompareTo(bSub);

        static (long, long) Split(string ts)
        {
            var dot = ts.IndexOf('.');
            var sec = long.TryParse(dot < 0 ? ts : ts[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var sub = dot < 0 ? 0 : long.TryParse(ts[(dot + 1)..].PadRight(6, '0')[..6], NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : 0;
            return (sec, sub);
        }
    }
}
