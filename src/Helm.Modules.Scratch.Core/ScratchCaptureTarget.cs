using Helm.Core.Capture;

namespace Helm.Modules.Scratch;

/// <summary>
/// Quick Capture into Scratch: the text goes in as it is typed (a link, an address, a code) and reaches the other
/// devices on the next sync. Text longer than <see cref="ScratchItem.MaxTextLength"/> is kept as a .txt file.
/// </summary>
public sealed class ScratchCaptureTarget(ScratchImporter importer) : ICaptureTarget
{
    public const string TargetId = "scratch";

    public string Id => TargetId;
    public string ModuleId => ScratchIds.ModuleId;
    public string Name => "Scratch";
    public string Prefix => "c";
    public string Example => "Anything to take out on another device: a link, an address, a code.";
    // After the others, so their Ctrl+1 … Ctrl+5 stay where they were.
    public int Order => 60;

    public CapturePreview Preview(string text)
    {
        if (text.Trim().Length == 0) return new CapturePreview(false, "Type the text.");
        var kept = text.Length > ScratchItem.MaxTextLength ? " · kept as a .txt file" : "";
        return new CapturePreview(true, $"New text in Scratch “{ScratchFormat.FirstLine(text, 60)}”{kept}");
    }

    public CaptureResult Capture(string text)
    {
        try
        {
            // Off the UI thread: long text is encrypted into a file, and its await must not wait for the blocked UI thread.
            Task.Run(() => importer.AddTextAsync(text)).GetAwaiter().GetResult();
            return new CaptureResult(true, $"Saved to Scratch: “{ScratchFormat.FirstLine(text, 60)}”.");
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, ex.Message);
        }
    }
}
