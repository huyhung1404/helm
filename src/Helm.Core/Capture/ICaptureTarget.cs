namespace Helm.Core.Capture;

/// <summary>What a capture would do with the typed text, shown under the box before it is saved.</summary>
/// <param name="CanSave">False when the text cannot be saved as it is (<paramref name="Text"/> says why).</param>
/// <param name="Text">E.g. "Task in To-do · due tomorrow 09:00 · High".</param>
public sealed record CapturePreview(bool CanSave, string Text);

/// <param name="Saved">False when nothing was saved (<paramref name="Message"/> says why).</param>
/// <param name="Message">E.g. "Added to To-do, due tomorrow 09:00."</param>
public sealed record CaptureResult(bool Saved, string Message);

/// <summary>
/// Somewhere Quick Capture can put a line of text: a note, a task, a debt. Each tool registers its targets in DI; Quick
/// Capture (and the Android share sheet) lists the targets of the tools that are turned on. Portable: no UI here.
/// Methods are called on the UI thread and must not throw (problems come back as a preview or result that says so).
/// </summary>
public interface ICaptureTarget
{
    /// <summary>Stable id, stored in settings as the default target, e.g. "note" or "task".</summary>
    string Id { get; }

    /// <summary>The tool this target writes to; the target is offered only while that tool is on.</summary>
    string ModuleId { get; }

    /// <summary>Short name on the target button, e.g. "Note".</summary>
    string Name { get; }

    /// <summary>Typed at the start of the text to pick this target, e.g. "t" for "/t buy milk".</summary>
    string Prefix { get; }

    /// <summary>One line that shows what can be typed, e.g. "buy milk tomorrow 9h #Home !".</summary>
    string Example { get; }

    /// <summary>Position among the targets (ascending).</summary>
    int Order { get; }

    CapturePreview Preview(string text);

    CaptureResult Capture(string text);
}
