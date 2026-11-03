using System.Globalization;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Provider;
using Helm.Core.Mcp;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// What Ask Helm can do on the phone itself: an alarm, a timer, a calendar event, the text just copied. Each hands
/// over to the phone's own app (Clock, Calendar) with the details filled in, where the user sees and can change it.
/// Made per card: the intents start from its activity.
/// </summary>
internal sealed class PhoneTools(Activity activity) : IMcpToolProvider
{
    public string? ModuleId => null;

    public IEnumerable<McpTool> Tools =>
    [
        new("phone_set_alarm", "Set an alarm in the phone's Clock app.",
            McpArgs.Schema(
                ("time", McpArgs.Text("The time, 24-hour: 06:30 or 21:00."), true),
                ("label", McpArgs.Text("What it is for, shown when it rings (optional)."), false)),
            (args, _) => Run(() => Alarm(args))),
        new("phone_set_timer", "Start a countdown timer in the phone's Clock app.",
            McpArgs.Schema(
                ("seconds", McpArgs.Number("How long, in seconds (600 for 10 minutes)."), true),
                ("label", McpArgs.Text("What it is for (optional)."), false)),
            (args, _) => Run(() => Timer(args))),
        new("phone_add_calendar_event", "Open the phone's calendar on a new event with these details filled in; the user saves it there.",
            McpArgs.Schema(
                ("title", McpArgs.Text("The event's title."), true),
                ("start", McpArgs.Text("When it starts, the user's local time: 2026-11-06T15:00."), true),
                ("end", McpArgs.Text("When it ends (default: an hour after the start)."), false),
                ("location", McpArgs.Text("Where (optional)."), false)),
            (args, _) => Run(() => Calendar(args))),
        new("phone_clipboard", "The text the user copied last on the phone (to save it somewhere, or to work on it).",
            McpTool.NoArguments(),
            (_, _) => Run(Clipboard)) { ReadOnly = true },
    ];

    private object Alarm(JsonElement args)
    {
        var time = McpArgs.RequiredString(args, "time").Trim();
        if (!TimeOnly.TryParseExact(time, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            throw new McpToolException($"{time} is not a time like 06:30.");
        var intent = new Intent(AlarmClock.ActionSetAlarm)
            .PutExtra(AlarmClock.ExtraHour, at.Hour)
            .PutExtra(AlarmClock.ExtraMinutes, at.Minute)
            .PutExtra(AlarmClock.ExtraSkipUi, false);
        if (McpArgs.String(args, "label") is { Length: > 0 } label) intent.PutExtra(AlarmClock.ExtraMessage, label);
        Start(intent, "a Clock app");
        return new { alarm = at.ToString("HH:mm", CultureInfo.InvariantCulture) };
    }

    private object Timer(JsonElement args)
    {
        var seconds = McpArgs.Int(args, "seconds") ?? throw new McpToolException("Say how many seconds.");
        if (seconds is < 1 or > 86_400) throw new McpToolException("A timer runs from 1 second to 24 hours.");
        var intent = new Intent(AlarmClock.ActionSetTimer)
            .PutExtra(AlarmClock.ExtraLength, seconds)
            .PutExtra(AlarmClock.ExtraSkipUi, false);
        if (McpArgs.String(args, "label") is { Length: > 0 } label) intent.PutExtra(AlarmClock.ExtraMessage, label);
        Start(intent, "a Clock app");
        return new { timer_seconds = seconds };
    }

    private object Calendar(JsonElement args)
    {
        var start = Time(McpArgs.RequiredString(args, "start"));
        var end = McpArgs.String(args, "end") is { Length: > 0 } e ? Time(e) : start.AddHours(1);
        var intent = new Intent(Intent.ActionInsert)
            .SetData(CalendarContract.Events.ContentUri)
            .PutExtra(CalendarContract.ExtraEventBeginTime, start.ToUnixTimeMilliseconds())
            .PutExtra(CalendarContract.ExtraEventEndTime, end.ToUnixTimeMilliseconds())
            .PutExtra(CalendarContract.Events.InterfaceConsts.Title, McpArgs.RequiredString(args, "title"));
        if (McpArgs.String(args, "location") is { Length: > 0 } location) intent.PutExtra(CalendarContract.Events.InterfaceConsts.EventLocation, location);
        Start(intent, "a calendar app");
        return new { opened = "calendar", start = start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) };
    }

    private object Clipboard()
    {
        var clipboard = (ClipboardManager?)activity.GetSystemService(Context.ClipboardService);
        var text = clipboard?.PrimaryClip is { ItemCount: > 0 } clip ? clip.GetItemAt(0)?.CoerceToText(activity) : null;
        return new { text = text is { Length: > 0 } ? text : null, empty = text is not { Length: > 0 } };
    }

    private static DateTimeOffset Time(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), ["yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            throw new McpToolException($"{text} is not a time like 2026-11-06T15:00.");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>Starts the phone's app (no package query needed: a missing app is the exception).</summary>
    private void Start(Intent intent, string what)
    {
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            throw new McpToolException($"This phone has no {what} for that.");
        }
    }

    /// <summary>The tools run on the assistant's thread; Android's own calls are made on the card's.</summary>
    private static Task<object?> Run(Func<object> work)
    {
        try
        {
            return Task.FromResult<object?>(work());
        }
        catch (Exception ex) when (ex is not McpToolException)
        {
            throw new McpToolException(ex.Message);
        }
    }
}
