using Helm.Core.Capture;
using Helm.Core.Modules;
using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

/// <summary>Quick Capture as its page sees it (on Windows: QuickCaptureModule): the settings, the targets and opening the box.</summary>
public interface IQuickCapture : IModule
{
    ISettingsStore<QuickCaptureSettings> Settings { get; }

    /// <summary>Every target any tool offers, whether the tool is on or not (for the settings page).</summary>
    IReadOnlyList<ICaptureTarget> AllTargets { get; }

    /// <summary>Opens the capture box. Any thread.</summary>
    void Open();
}
