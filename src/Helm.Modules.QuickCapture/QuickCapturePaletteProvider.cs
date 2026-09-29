using Helm.Core.Capture;
using Helm.Core.Palette;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Quick Capture in the command palette: "Quick Capture" opens the box, and whatever is typed can be saved as a note,
/// a task or a debt (listed last, under the real results). Choosing one opens the box with the text, to check it and
/// press Enter.
/// </summary>
internal sealed class QuickCapturePaletteProvider(QuickCaptureModule module) : IPaletteProvider
{
    /// <summary>Below every real match, so they only lead when nothing else matches.</summary>
    private const double SaveAsScore = 0.02;

    public string? ModuleId => QuickCaptureModule.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        var open = query.IsEmpty ? 0.05 : Math.Max(query.Score("Quick Capture"), query.Score("Ghi nhanh"));
        if (open > 0) yield return new PaletteItem("Quick Capture", "Save a note, a task or a debt", PaletteKind.Action, open, () => module.Open());
        if (query.Text.Length < 2) yield break;

        var targets = module.AvailableTargets();
        // "/t buy milk" already names its target.
        var (prefixed, rest) = CaptureRouter.SplitPrefix(targets, query.Text);
        foreach (var target in prefixed is null ? targets : new[] { prefixed })
        {
            CapturePreview preview;
            try
            {
                preview = target.Preview(rest);
            }
            catch (Exception)
            {
                continue;
            }
            yield return new PaletteItem($"Save as {target.Name.ToLowerInvariant()}: {query.Text}", preview.Text, PaletteKind.Action,
                prefixed is null ? SaveAsScore : 2, () => module.Open(query.Text, target.Id));
        }
    }
}
