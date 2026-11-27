using Android.Content;
using Android.Content.PM;
using FluentIcons.Common;
using Helm.Core.Capture;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Quick Capture on Android: "Save to Helm" in the share sheet of any app and a Quick Settings tile. Both open
/// <see cref="QuickCaptureActivity"/>, a small dialog over the current app. While the tool is off, both are hidden
/// (their components are disabled), so Helm does not show up in the share sheet.
/// </summary>
public sealed class QuickCaptureModule(ILogger<QuickCaptureModule> logger, ISettingsStoreFactory settings, IServiceProvider services)
    : AndroidModuleBase, IQuickCapture
{
    public const string ModuleId = "quick-capture";

    /// <summary>Stable component names: Android remembers enabled states and pinned tiles by name.</summary>
    public const string ActivityName = "com.huyhung1404.helm.QuickCaptureActivity";
    public const string TileName = "com.huyhung1404.helm.QuickCaptureTile";

    public override string Id => ModuleId;
    public override string DisplayName => "Quick Capture";
    public override string Description => "Save a note, a task or a debt from any app: Share → Save to Helm, or the Quick Settings tile.";
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.Flash;

    /// <summary>The same vector icon as on Windows (<see cref="QuickCaptureIconShape"/>).</summary>
    public override Avalonia.Media.IImage? IconImage => QuickCaptureIcon.Image;
    public override Type PageType => typeof(QuickCapturePage);

    /// <summary>settings/quick-capture.json, shared with Windows (the shortcut there is unused here).</summary>
    public ISettingsStore<QuickCaptureSettings> Settings { get; } = settings.Get<QuickCaptureSettings>(ModuleId);

    /// <summary>Every tool's target, read when the page asks (the tools register them after this module).</summary>
    public IReadOnlyList<ICaptureTarget> AllTargets =>
        [.. ((IEnumerable<ICaptureTarget>?)services.GetService(typeof(IEnumerable<ICaptureTarget>)) ?? []).OrderBy(t => t.Order)];

    /// <summary>Opens the capture dialog, as the tile does.</summary>
    public void Open()
    {
        try
        {
            var context = AndroidApp.Context;
            context.StartActivity(new Intent(context, typeof(QuickCaptureActivity)).AddFlags(ActivityFlags.NewTask));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the Quick Capture dialog");
        }
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        SetComponents(true);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        SetComponents(false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether a tool is on, read from the saved settings: the share dialog and the tile can run without Helm's
    /// activity, before the module registry has started (as the Tracker widget does).
    /// </summary>
    public static bool IsModuleEnabled(ISettingsStoreFactory settings, string moduleId) =>
        !settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules.TryGetValue(moduleId, out var on) || on;

    /// <summary>The targets of the tools that are on now.</summary>
    public static IReadOnlyList<ICaptureTarget> AvailableTargets(IEnumerable<ICaptureTarget> targets, ISettingsStoreFactory settings) =>
        CaptureRouter.Available(targets, id => IsModuleEnabled(settings, id));

    private void SetComponents(bool enabled)
    {
        try
        {
            var context = AndroidApp.Context;
            var state = enabled ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
            foreach (var name in new[] { ActivityName, TileName })
                context.PackageManager?.SetComponentEnabledSetting(new ComponentName(context, name), state, ComponentEnableOption.DontKillApp);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not {Action} the Quick Capture share target and tile", enabled ? "show" : "hide");
        }
    }
}
