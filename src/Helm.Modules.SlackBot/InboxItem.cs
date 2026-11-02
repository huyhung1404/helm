using System.Text.Json.Serialization;

namespace Helm.Modules.SlackBot;

/// <summary>What Claude made of a message.</summary>
public enum InboxLevel
{
    /// <summary>Not sorted yet.</summary>
    Pending,
    /// <summary>The user has to answer or do something.</summary>
    Act,
    /// <summary>Worth knowing; nothing to do.</summary>
    Know,
    /// <summary>Not for the user (chatter, bots, other teams' work).</summary>
    Skip,
}

/// <summary>Why an item left the to-do list.</summary>
public enum HandledBy
{
    None,
    /// <summary>The user wrote in that DM or thread afterwards (from any device).</summary>
    Replied,
    /// <summary>The user put an emoji on it, or on a later message of the same DM or thread.</summary>
    Reacted,
    /// <summary>The user pressed Done.</summary>
    Dismissed,
}

/// <summary>One message someone else wrote, as SlackBot keeps it (only on this PC, encrypted).</summary>
public sealed class InboxItem
{
    public string Channel { get; set; } = "";

    public string Ts { get; set; } = "";

    /// <summary>The thread it is in (its parent's ts), or null for a message outside threads.</summary>
    public string? ThreadTs { get; set; }

    public SlackConversationKind Kind { get; set; }

    /// <summary>Where it was written, for people: "#release", "DM", "An, Bình".</summary>
    public string Where { get; set; } = "";

    /// <summary>Who wrote it (display name).</summary>
    public string From { get; set; } = "";

    /// <summary>The words, as a person reads them (<see cref="SlackText.Plain"/>).</summary>
    public string Text { get; set; } = "";

    /// <summary>The thread's first message, when this is a reply (for context), clipped.</summary>
    public string? ThreadStart { get; set; }

    /// <summary>The message calls the user by name (@them): kept even from a conversation that is off.</summary>
    public bool Tagged { get; set; }

    /// <summary>For the user: a DM or group DM, a message that calls them, or a reply in their thread.</summary>
    public bool Direct { get; set; }

    public InboxLevel Level { get; set; }

    /// <summary>Claude's one line about it, in Vietnamese.</summary>
    public string? Summary { get; set; }

    /// <summary>Runs that left it unsorted (Claude skipped it); after a few it is shown as it is.</summary>
    public int SortTries { get; set; }

    public HandledBy Handled { get; set; }

    public DateTimeOffset? HandledAt { get; set; }

    /// <summary>A worth-knowing item the user has seen in Slack Bot.</summary>
    public bool Read { get; set; }

    /// <summary>The reply Slack Bot sent for the user (it stays on the list all the same), or null.</summary>
    public string? BotReply { get; set; }

    public DateTimeOffset? BotReplyAt { get; set; }

    [JsonIgnore]
    public string Id => SlackIds.Message(Channel, Ts);

    [JsonIgnore]
    public DateTimeOffset Time => SlackIds.Time(Ts);

    /// <summary>The DM, or the thread, or the channel outside threads: where a reply by the user answers it.</summary>
    [JsonIgnore]
    public string Scope => SlackInbox.ScopeOf(Channel, Kind, ThreadTs);

    /// <summary>Shown on screen until handled: to do, or for the user and not sorted yet (so a broken Claude never hides it).</summary>
    [JsonIgnore]
    public bool IsToDo => Handled == HandledBy.None && (Level == InboxLevel.Act || (Level == InboxLevel.Pending && Direct));
}

/// <summary>A level the user changed by hand: shown to Claude next time, so it learns what matters to them.</summary>
public sealed record InboxCorrection(string Where, string Text, InboxLevel Level);

/// <summary>Everything SlackBot remembers, saved as one file.</summary>
public sealed class InboxData
{
    public List<InboxItem> Items { get; set; } = [];

    /// <summary>The workspace's address (https://team.slack.com/), for links before Slack is reached again.</summary>
    public string? WorkspaceUrl { get; set; }

    /// <summary>The last message seen while connected: catching up starts after it.</summary>
    public string? LastSeen { get; set; }

    /// <summary>The user's last message per <see cref="InboxItem.Scope"/>.</summary>
    public Dictionary<string, string> LastMine { get; set; } = [];

    /// <summary>The time of the last report (the times set on the settings page).</summary>
    public DateTimeOffset? LastDigest { get; set; }

    public List<InboxCorrection> Corrections { get; set; } = [];

    /// <summary>The latest message seen per conversation (anyone's): catching up reads the active ones first.</summary>
    public Dictionary<string, string> Activity { get; set; } = [];

    /// <summary>Every DM's latest message was looked up once: <see cref="Activity"/> knows which DMs are quiet.</summary>
    public bool ActivityLearned { get; set; }

    /// <summary>Messages Slack Bot sent (channel:ts): they are the user's account but not the user's answer.</summary>
    public List<string> BotMessages { get; set; } = [];

    /// <summary>When Slack Bot last replied per <see cref="InboxItem.Scope"/>: once in a while per DM or thread.</summary>
    public Dictionary<string, DateTimeOffset> BotScopes { get; set; } = [];
}
