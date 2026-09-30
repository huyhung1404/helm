using Helm.Core.Capture;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Quick Capture into Watch Later: the first link in the text is the video, the rest becomes its note. Sharing a
/// YouTube or Facebook link to Helm picks this target by itself (<see cref="Claims"/>).
/// </summary>
public sealed class WatchLaterCaptureTarget(WatchLaterStore store) : ICaptureTarget
{
    public const string TargetId = "watch";

    public string Id => TargetId;
    public string ModuleId => WatchLaterIds.ModuleId;
    public string Name => "Watch later";
    public string Prefix => "w";
    public string Example => "A YouTube or Facebook link, then why you save it (optional).";
    public int Order => 30;

    public bool Claims(string text) => VideoLink.Find(text) is { Source: not WatchSource.Other };

    public CapturePreview Preview(string text)
    {
        if (VideoLink.Find(text) is not { } link) return new CapturePreview(false, "Paste a YouTube or Facebook link.");
        var note = VideoLink.TextAround(text, link);
        if (note.Length > WatchItem.MaxNoteLength) return new CapturePreview(false, $"The note is too long (up to {WatchItem.MaxNoteLength:N0} characters).");
        if (store.Find(link.Key) is { } existing)
            return new CapturePreview(true, $"Already saved: “{WatchLaterFormat.Shorten(existing.Value.DisplayTitle, 60)}”. Saving moves it to the top.");
        var what = $"New {WatchLaterFormat.KindName(link.Source, link.Kind)} in Watch Later";
        return new CapturePreview(true, note.Length > 0 ? $"{what} · note “{WatchLaterFormat.Shorten(note.Split('\n')[0], 40)}”" : what);
    }

    public CaptureResult Capture(string text)
    {
        if (VideoLink.Find(text) is not { } link) return new CaptureResult(false, "There is no link to save.");
        try
        {
            var result = store.Add(link, VideoLink.TextAround(text, link));
            return new CaptureResult(true, result.Existed ? "Already in Watch Later: moved to the top." : "Saved to Watch Later.");
        }
        catch (ArgumentException ex)
        {
            return new CaptureResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, $"Could not save the video: {ex.Message}");
        }
    }
}
