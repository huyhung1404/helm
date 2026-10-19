namespace Helm.Core.Capture;

/// <summary>
/// Turns a piece of text into a note. Notes provides it; other tools (Scratch's Send to Notes) use it without
/// depending on Notes itself. Absent when Notes is not part of the app.
/// </summary>
public interface INoteWriter
{
    /// <summary>The tool that keeps the notes (its on/off state decides whether writing is offered).</summary>
    string ModuleId { get; }

    /// <summary>
    /// Saves <paramref name="text"/> as a new note, unchanged: the note shows its first line as the title.
    /// </summary>
    /// <returns>The new note's id and the title it shows.</returns>
    /// <exception cref="ArgumentException">The text is empty or too long for one note.</exception>
    (string Id, string Title) Write(string text);
}
