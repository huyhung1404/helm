using System.Text.Json;
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

    public static Spec Navigate { get; } = new("phone_navigate",
        "Open the phone's maps app on directions to a place (an address or a place's name), ready to start.",
        McpArgs.Schema(
            ("destination", McpArgs.Text("Where to, as the user said it: \"36 Hoàng Cầu, Hà Nội\", \"Vincom Bà Triệu\"."), true),
            ("mode", McpArgs.OneOf("How the user goes (default: by motorbike).", "motorbike", "car", "walk", "transit"), false)), false);

    public static Spec OpenApp { get; } = new("phone_open_app",
        "Open an app installed on the phone by its name (Grab, YouTube, Zalo…). When several apps fit the name, nothing opens and the answer lists them: ask the user which one.",
        McpArgs.Schema(("app", McpArgs.Text("The app's name as the user said it."), true)), false);

    public static Spec ShareText { get; } = new("phone_share_text",
        "Open the phone's share sheet with a text (Messenger, Zalo, SMS…): the user picks the app and sends it there; Helm never sends it. Its summary is the text itself, so the card shows it.",
        McpArgs.Schema(
            ("text", McpArgs.Text("The text to share."), true),
            ("title", McpArgs.Text("A short title for the share sheet (optional)."), false)), false);

    public static IReadOnlyList<Spec> All { get; } = [Alarm, Timer, CalendarEvent, Clipboard, Navigate, OpenApp, ShareText];

    /// <summary>The tool as a PC offers it: the call is only recorded, for the phone to run when the answer arrives.</summary>
    /// <summary>The tool as the phone runs it, with the phone's own code.</summary>
    public static McpTool On(Spec spec, Func<JsonElement, CancellationToken, Task<object?>> run) =>
        new(spec.Name, spec.Description, (JsonObject)spec.Schema.DeepClone(), run) { ReadOnly = spec.ReadOnly };

    internal static McpTool HandedBack(Spec spec) =>
        new(spec.Name, spec.Description, (JsonObject)spec.Schema.DeepClone(), (_, _) => Task.FromResult<object?>(new { handed_to_phone = true }));
}
