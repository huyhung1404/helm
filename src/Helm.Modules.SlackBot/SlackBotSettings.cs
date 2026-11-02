using Helm.Core.Settings;

namespace Helm.Modules.SlackBot;

/// <summary>SlackBot's options, on this PC only (never synced: they name the workspace's channels).</summary>
public sealed class SlackBotSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly IReadOnlyList<string> DefaultReportTimes = ["12:00", "17:00"];

    public int Version { get; set; }

    /// <summary>Channels and DMs SlackBot never reads: nothing from them is kept or shown to Claude.</summary>
    public List<string> Blocked { get; set; } = [];

    /// <summary>Times of day (HH:mm) when the worth-knowing messages show up on screen.</summary>
    public List<string> ReportTimes { get; set; } = [.. DefaultReportTimes];

    /// <summary>How often channel messages (not for the user) are sorted; messages for the user are sorted at once.</summary>
    public int ChannelMinutes { get; set; } = 30;

    /// <summary>Messages from bots and apps are read (only those that call the user otherwise).</summary>
    public bool ReadBots { get; set; }

    /// <summary>What matters to the user, in their own words: given to Claude with every batch.</summary>
    public string AboutMe { get; set; } = "";

    /// <summary>Claude Code model for sorting ("" = the user's default, "haiku", "sonnet").</summary>
    public string Model { get; set; } = "";

    /// <summary>A sound when something new lands on the to-do list.</summary>
    public bool PlaySound { get; set; }

    /// <summary>Where the user dragged the overlay (its bottom-left corner), or null for the bottom-right corner.</summary>
    public double? OverlayLeft { get; set; }

    public double? OverlayBottom { get; set; }

    /// <summary>The overlay is folded into its small badge.</summary>
    public bool OverlayFolded { get; set; }
}
