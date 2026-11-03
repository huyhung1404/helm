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

    // Ask Helm's own tools, so their texts are short already (every call pays for them): the names say the rest.

    public static Spec Alarm { get; } = new("phone_set_alarm", "Set an alarm in the phone's Clock app.",
        McpArgs.Schema(("time", McpArgs.Text("24 h: 06:30"), true), ("label", McpArgs.Text("shown when it rings"), false)), false);

    public static Spec Timer { get; } = new("phone_set_timer", "Start a countdown timer in the phone's Clock app.",
        McpArgs.Schema(("seconds", McpArgs.Number("600 for 10 minutes"), true), ("label", Plain(), false)), false);

    public static Spec CalendarEvent { get; } = new("phone_add_calendar_event",
        "Open the phone's calendar on a new event with these details; the user saves it there.",
        McpArgs.Schema(
            ("title", Plain(), true),
            ("start", McpArgs.Text("local: 2026-11-06T15:00"), true),
            ("end", McpArgs.Text("default an hour after the start"), false),
            ("location", Plain(), false)), false);

    public static Spec Clipboard { get; } = new("phone_clipboard", "The text the user copied last on the phone.", McpTool.NoArguments(), true);

    public static Spec Navigate { get; } = new("phone_navigate",
        "Open the phone's maps app on directions to a place (an address or a name), ready to start.",
        McpArgs.Schema(
            ("destination", McpArgs.Text("as the user said it: 36 Hoàng Cầu, Vincom Bà Triệu"), true),
            ("mode", McpArgs.OneOf("default motorbike", "motorbike", "car", "walk", "transit"), false)), false);

    public static Spec OpenApp { get; } = new("phone_open_app",
        "Open an app on the phone by its name (Grab, YouTube, Zalo…). When several fit, nothing opens and the answer lists them: ask which.",
        McpArgs.Schema(("app", McpArgs.Text("its name as the user said it"), true)), false);

    public static Spec ShareText { get; } = new("phone_share_text",
        "Open the phone's share sheet with a text: the user picks the app (Messenger, Zalo, SMS…) and sends it; Helm never does. Its summary is the text itself.",
        McpArgs.Schema(("text", Plain(), true), ("title", McpArgs.Text("for the share sheet"), false)), false);

    public static IReadOnlyList<Spec> All { get; } = [Alarm, Timer, CalendarEvent, Clipboard, Navigate, OpenApp, ShareText];

    /// <summary>The tool as the phone runs it, with the phone's own code.</summary>
    public static McpTool On(Spec spec, Func<JsonElement, CancellationToken, Task<object?>> run) =>
        new(spec.Name, spec.Description, (JsonObject)spec.Schema.DeepClone(), run) { ReadOnly = spec.ReadOnly };

    /// <summary>The tool as a PC offers it: the call is only recorded, for the phone to run when the answer arrives.</summary>
    internal static McpTool HandedBack(Spec spec) =>
        new(spec.Name, spec.Description, (JsonObject)spec.Schema.DeepClone(), (_, _) => Task.FromResult<object?>(new { handed_to_phone = true }));

    /// <summary>A text argument whose name says what it is.</summary>
    private static JsonObject Plain() => new() { ["type"] = "string" };
}
