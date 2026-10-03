using Helm.Core.Capture;

namespace Helm.Modules.Missions;

/// <summary>
/// Quick Capture and Android's Share → "Save to Helm" for missions: an AI's answer with a mission in it (the JSON the
/// prompt asks for) becomes a new, not yet started mission, to check and start on the Missions page. Shared text that
/// imports as a mission picks this target by itself; "/m" picks it by hand.
/// </summary>
public sealed class MissionCaptureTarget(MissionsStore store) : ICaptureTarget
{
    public const string TargetId = "mission";

    public string Id => TargetId;
    public string ModuleId => MissionsIds.ModuleId;
    public string Name => "Mission";
    public string Prefix => "m";
    public string Example => "Paste an AI's answer to the Missions prompt: the mission in it is created, ready to start.";
    public int Order => 40;

    /// <summary>A shared answer that holds a mission (cheap check first: most shared text has no JSON at all).</summary>
    public bool Claims(string text) =>
        text.Contains('{')
        && (text.Contains("phases", StringComparison.OrdinalIgnoreCase) || text.Contains("steps", StringComparison.OrdinalIgnoreCase))
        && MissionImport.Parse(text).Ok;

    public CapturePreview Preview(string text)
    {
        if (text.Trim().Length == 0) return new(false, Example);
        var result = MissionImport.Parse(text);
        if (!result.Ok) return new(false, result.Errors.FirstOrDefault() ?? "This is not a mission.");
        var draft = result.Draft!;
        return new(true, $"New mission “{draft.Title}”: {Plural(draft.Phases.Count, "phase")}, {Plural(draft.StepCount, "step")}, about {MissionsFormat.Days(draft.EstimateDays)}");
    }

    public CaptureResult Capture(string text)
    {
        try
        {
            var result = MissionImport.Parse(text);
            if (!result.Ok) return new(false, result.Errors.FirstOrDefault() ?? "This is not a mission.");
            store.Create(result.Draft!, MissionSource.Import);
            return new(true, $"Mission “{result.Draft!.Title}” saved. Open Missions to check it and start.");
        }
        catch (ArgumentException ex)
        {
            return new(false, ex.Message);
        }
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
