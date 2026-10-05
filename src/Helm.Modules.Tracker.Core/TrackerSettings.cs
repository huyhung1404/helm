using Helm.Core.Settings;

namespace Helm.Modules.Tracker;

/// <summary>
/// Device-local view preferences (the data itself is synced through <see cref="TrackerStore"/>). Stored in
/// settings/tracker.json on both apps.
/// </summary>
public sealed class TrackerSettings : IVersionedSettings
{
    public static int CurrentVersion => 2;

    public int Version { get; set; }

    /// <summary>The workspace the page opens on; null picks the first one.</summary>
    public string? SelectedWorkspaceId { get; set; }

    public bool ShowCompleted { get; set; } = true;

    public ReportRange ReportRange { get; set; } = ReportRange.Last7Days;

    /// <summary>The report covers every workspace instead of the selected one.</summary>
    public bool ReportAllWorkspaces { get; set; }

    /// <summary>A daily notification about open items that are due soon or overdue.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Local hour (0–23) from which the day's reminder may be shown.</summary>
    public int ReminderHour { get; set; } = 9;

    /// <summary>How many days before the due date an item counts as "due soon" (0 = only on the day).</summary>
    public int RemindDaysBefore { get; set; } = 1;

    /// <summary>The local date of the last reminder shown on this device (one per day).</summary>
    public DateOnly? LastReminderDate { get; set; }

    /// <summary>Android widget background (see <see cref="WidgetLook"/>).</summary>
    public WidgetBackground WidgetBackground { get; set; } = WidgetBackground.Card;

    /// <summary>Android widget background opacity, 0 (see-through) to 100 (solid).</summary>
    public int WidgetOpacity { get; set; } = 100;

    public WidgetText WidgetText { get; set; } = WidgetText.Automatic;

    public void Migrate(int fromVersion)
    {
        // v2: Transparent became the theme's colour at the chosen opacity (it used to ignore the opacity and be fully
        // clear), so a saved Transparent keeps looking see-through instead of turning solid.
        if (fromVersion < 2 && WidgetBackground == WidgetBackground.Transparent && WidgetOpacity > 60)
            WidgetOpacity = WidgetLook.TransparentOpacity;
    }
}

/// <summary>The Tracker widget's colours, from its settings (see <see cref="WidgetLook"/>).</summary>
public static class TrackerWidgetStyle
{
    public static WidgetLook Look(TrackerSettings s) => new(s.WidgetBackground, s.WidgetOpacity, s.WidgetText);

    /// <summary>The background colour (solid; see <see cref="Alpha"/>); null for the theme's card colour.</summary>
    public static uint? Background(TrackerSettings s) => Look(s).BackgroundColor;

    /// <summary>0-255 opacity of the background layer.</summary>
    public static int Alpha(TrackerSettings s) => Look(s).Alpha;

    /// <summary>Main and secondary text colours; null for the theme's.</summary>
    public static (uint Main, uint Secondary)? Text(TrackerSettings s) => Look(s).TextColors;
}
