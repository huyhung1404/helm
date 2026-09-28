using Helm.Core.Settings;

namespace Helm.Modules.Tracker;

/// <summary>
/// Device-local view preferences (the data itself is synced through <see cref="TrackerStore"/>). Stored in
/// settings/tracker.json on both apps.
/// </summary>
public sealed class TrackerSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

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

    /// <summary>Android widget background (see <see cref="TrackerWidgetStyle"/>).</summary>
    public TrackerWidgetBackground WidgetBackground { get; set; } = TrackerWidgetBackground.Card;

    /// <summary>Android widget background opacity, 0 (see-through) to 100 (solid).</summary>
    public int WidgetOpacity { get; set; } = 100;

    public TrackerWidgetText WidgetText { get; set; } = TrackerWidgetText.Automatic;
}

public enum TrackerWidgetBackground
{
    /// <summary>The app's card colour (light or dark with the phone's theme).</summary>
    Card,
    Light,
    Dark,
    Accent,
    Transparent,
}

public enum TrackerWidgetText
{
    /// <summary>Readable on the background (dark text on light, light text on dark).</summary>
    Automatic,
    Dark,
    Light,
}

/// <summary>The widget's colours as ARGB, from the settings (null: use the theme's colour resource).</summary>
public static class TrackerWidgetStyle
{
    public static readonly IReadOnlyList<string> BackgroundNames = ["Card (follows the theme)", "Light", "Dark", "Accent", "Transparent"];

    public static readonly IReadOnlyList<string> TextNames = ["Automatic", "Dark", "Light"];

    /// <summary>The background colour with the opacity applied; null for the theme's card colour at that opacity.</summary>
    public static uint? Background(TrackerSettings s) => s.WidgetBackground switch
    {
        TrackerWidgetBackground.Light => WithAlpha(0xFFFBFBFB, s.WidgetOpacity),
        TrackerWidgetBackground.Dark => WithAlpha(0xFF202020, s.WidgetOpacity),
        TrackerWidgetBackground.Accent => WithAlpha(0xFF0067C0, s.WidgetOpacity),
        TrackerWidgetBackground.Transparent => 0x00000000,
        _ => null,
    };

    /// <summary>0-255 opacity of the background layer.</summary>
    public static int Alpha(TrackerSettings s) =>
        s.WidgetBackground == TrackerWidgetBackground.Transparent ? 0 : Byte(s.WidgetOpacity);

    /// <summary>Main and secondary text colours; null for the theme's.</summary>
    public static (uint Main, uint Secondary)? Text(TrackerSettings s)
    {
        var light = s.WidgetText switch
        {
            TrackerWidgetText.Light => true,
            TrackerWidgetText.Dark => false,
            // Automatic: light text on the dark and accent backgrounds; the theme decides on the card colour.
            _ => s.WidgetBackground switch
            {
                TrackerWidgetBackground.Dark or TrackerWidgetBackground.Accent => true,
                TrackerWidgetBackground.Light => false,
                _ => (bool?)null,
            },
        };
        return light switch
        {
            true => (0xFFFFFFFF, 0xCCFFFFFF),
            false => (0xFF1A1A1A, 0xFF5F5F5F),
            _ => null,
        };
    }

    private static uint WithAlpha(uint argb, int opacity) => (argb & 0x00FFFFFF) | ((uint)Byte(opacity) << 24);

    /// <summary>0-100 % as 0-255, rounded (integer maths: 50 % is 128).</summary>
    private static int Byte(int opacity) => (Math.Clamp(opacity, 0, 100) * 255 + 50) / 100;
}
