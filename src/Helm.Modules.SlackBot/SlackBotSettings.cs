using Helm.Core.Settings;

namespace Helm.Modules.SlackBot;

/// <summary>What Slack Bot may do in a conversation.</summary>
public enum SlackChannelMode
{
    /// <summary>Not read: nothing from it is kept or shown to Claude.</summary>
    Off,
    /// <summary>Read and reported; never written to.</summary>
    Read,
    /// <summary>Read, and messages for the user get the reply template (as an AI assistant, openly).</summary>
    Reply,
}

/// <summary>Slack Bot's options, on this PC only (never synced: they name the workspace's channels).</summary>
public sealed class SlackBotSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly IReadOnlyList<string> DefaultReportTimes = ["12:00", "17:00"];

    /// <summary>Put before every reply by Helm (not by Claude), so nobody takes it for the user; {me} is their name.</summary>
    public const string DefaultSignature = "🤖 Lumi (trợ lý AI của {me}):";

    public int Version { get; set; }

    /// <summary>
    /// What Slack Bot may do per conversation, set by the user. Not listed: DMs and group DMs are read, channels are
    /// off (the user turns on the channels they want).
    /// </summary>
    public Dictionary<string, SlackChannelMode> Channels { get; set; } = [];

    /// <summary>Times of day (HH:mm) when the worth-knowing messages show up on screen.</summary>
    public List<string> ReportTimes { get; set; } = [.. DefaultReportTimes];

    /// <summary>Seconds between sorting runs for channel messages (not for the user); those for the user are sorted at once.</summary>
    public int ChannelSeconds { get; set; } = DefaultChannelSeconds;

    public const int DefaultChannelSeconds = 1800;

    /// <summary>Shorter than this would run Claude Code almost without a break.</summary>
    public const int MinChannelSeconds = 10;

    /// <summary>Messages from bots and apps are read (only those that call the user otherwise).</summary>
    public bool ReadBots { get; set; }

    /// <summary>What matters to the user, in their own words: given to Claude with every batch.</summary>
    public string AboutMe { get; set; } = "";

    /// <summary>Claude Code model for sorting ("" = the user's default, "haiku", "sonnet").</summary>
    public string Model { get; set; } = "";

    /// <summary>The AI signature every reply starts with; {me} is the user's name. It always starts with 🤖.</summary>
    public string Signature { get; set; } = DefaultSignature;

    /// <summary>How the user wants replies written (tone, language, what to say), in their own words; may be empty.</summary>
    public string ReplyStyle { get; set; } = "";

    /// <summary>A message waits this long for the user to answer it before the reply goes.</summary>
    public int ReplyAfterMinutes { get; set; } = 3;

    /// <summary>A sound when something new lands on the to-do list.</summary>
    public bool PlaySound { get; set; }

    /// <summary>How opaque the list on screen is while the mouse is not on it (0.3-1).</summary>
    public double OverlayOpacity { get; set; } = 0.8;

    /// <summary>Where the user dragged the overlay (its bottom-left corner), or null for the bottom-right corner.</summary>
    public double? OverlayLeft { get; set; }

    public double? OverlayBottom { get; set; }

    /// <summary>The overlay is folded into its small badge.</summary>
    public bool OverlayFolded { get; set; }

    /// <summary>The user's choice for a conversation, or its default.</summary>
    public SlackChannelMode ModeOf(string id, SlackConversationKind kind) =>
        Channels.TryGetValue(id, out var mode) ? mode : DefaultMode(kind);

    public static SlackChannelMode DefaultMode(SlackConversationKind kind) =>
        kind is SlackConversationKind.DirectMessage or SlackConversationKind.GroupMessage ? SlackChannelMode.Read : SlackChannelMode.Off;
}
