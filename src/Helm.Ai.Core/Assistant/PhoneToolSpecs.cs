using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Ai.Assistant;

/// <summary>
/// What the phone's own tools take, in one place: the phone runs them (Helm.Modules.QuickCapture's PhoneTools), and an
/// agent on a PC is offered the same ones and hands its calls back for the phone (it cannot read the phone, so the
/// clipboard is not offered there).
/// </summary>
public static class PhoneToolSpecs
{
    public sealed record Spec(string Name, string Description, JsonObject Schema, bool ReadOnly);

    public static Spec Alarm { get; } = new("phone_set_alarm", "Set an alarm in the phone's Clock app.",
        McpArgs.Schema(
            ("time", McpArgs.Text("The time, 24-hour: 06:30 or 21:00."), true),
            ("label", McpArgs.Text("What it is for, shown when it rings (optional)."), false)), false);

    public static Spec Timer { get; } = new("phone_set_timer", "Start a countdown timer in the phone's Clock app.",
        McpArgs.Schema(
            ("seconds", McpArgs.Number("How long, in seconds (600 for 10 minutes)."), true),
            ("label", McpArgs.Text("What it is for (optional)."), false)), false);

    public static Spec CalendarEvent { get; } = new("phone_add_calendar_event",
        "Open the phone's calendar on a new event with these details filled in; the user saves it there.",
        McpArgs.Schema(
            ("title", McpArgs.Text("The event's title."), true),
            ("start", McpArgs.Text("When it starts, the user's local time: 2026-11-06T15:00."), true),
            ("end", McpArgs.Text("When it ends (default: an hour after the start)."), false),
            ("location", McpArgs.Text("Where (optional)."), false)), false);

    public static Spec Clipboard { get; } = new("phone_clipboard",
        "The text the user copied last on the phone (to save it somewhere, or to work on it).", McpTool.NoArguments(), true);

    public static IReadOnlyList<Spec> All { get; } = [Alarm, Timer, CalendarEvent, Clipboard];

    /// <summary>The tool as a PC offers it: the call is only recorded, for the phone to run when the answer arrives.</summary>
    internal static McpTool HandedBack(Spec spec) =>
        new(spec.Name, spec.Description, (JsonObject)spec.Schema.DeepClone(), (_, _) => Task.FromResult<object?>(new { handed_to_phone = true }));
}
