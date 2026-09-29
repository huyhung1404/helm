using Helm.Core.Capture;

namespace Helm.Modules.Notes;

/// <summary>
/// Quick Capture into Notes: the first line becomes the title when there are several lines, otherwise the text is the
/// note (the list then shows its first words as the title).
/// </summary>
public sealed class NoteCaptureTarget(NotesStore store) : ICaptureTarget
{
    public const string TargetId = "note";

    public string Id => TargetId;
    public string ModuleId => NotesIds.ModuleId;
    public string Name => "Note";
    public string Prefix => "n";
    public string Example => "Anything. Several lines: the first one is the title.";
    public int Order => 0;

    public CapturePreview Preview(string text)
    {
        var (title, body) = Split(text);
        if (title.Length == 0 && body.Length == 0) return new CapturePreview(false, "Type the note.");
        if (title.Length + body.Length > NoteItem.MaxLength) return new CapturePreview(false, $"Too long for one note (up to {NoteItem.MaxLength:N0} characters).");
        return new CapturePreview(true, $"New note “{NoteText.DisplayTitle(title, body)}”");
    }

    public CaptureResult Capture(string text)
    {
        var (title, body) = Split(text);
        try
        {
            store.Add(title, body);
            return new CaptureResult(true, $"Saved to Notes: “{NoteText.DisplayTitle(title, body)}”.");
        }
        catch (ArgumentException ex)
        {
            return new CaptureResult(false, ex.Message);
        }
    }

    /// <summary>Several lines: (first line, the rest). One line: ("", the line).</summary>
    internal static (string Title, string Body) Split(string text)
    {
        var lines = NoteText.Normalize(text).Trim().Split('\n');
        if (lines.Length < 2) return ("", lines[0].Trim());
        return (lines[0].Trim(), string.Join('\n', lines.Skip(1)).Trim());
    }
}
