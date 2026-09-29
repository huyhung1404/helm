using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

/// <summary>Stored in settings/quick-capture.json, like on Windows (the shortcut exists only there).</summary>
public sealed class QuickCaptureSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The target selected when the dialog opens (a target id such as "note" or "task").</summary>
    public string DefaultTarget { get; set; } = "note";
}
