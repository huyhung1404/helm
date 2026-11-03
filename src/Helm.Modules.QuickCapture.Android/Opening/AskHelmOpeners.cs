using Android.Content;
using Android.OS;
using Helm.Ai.Assistant;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlarmManager = Android.App.AlarmManager;
using AlarmType = Android.App.AlarmType;
using AndroidApp = Android.App.Application;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

[assembly: Android.App.UsesPermission("android.permission.RECEIVE_BOOT_COMPLETED")]

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The other ways to open Ask Helm, kept as <see cref="AskHelmOpenSettings"/> and the tool's on/off say: the edge handle,
/// the "Ask Helm" icon and the widget's buttons. Started with Quick Capture, after a reboot or an update of Helm
/// (<see cref="AskHelmOpenReceiver"/>), and by an hourly alarm that puts the handle back after Android stopped Helm, as
/// Vocabulary's bubble does; the alarm never wakes the phone and is set only while the handle is on.
/// </summary>
public sealed class AskHelmOpeners
{
    private readonly ISettingsStore<AskHelmOpenSettings> _store;
    private readonly ISettingsStoreFactory _settings;
    private readonly AskHelmEdgeHandle _edge;
    private readonly ILogger<AskHelmOpeners> _logger;
    private readonly Context _context = AndroidApp.Context;
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _following;

    public AskHelmOpeners(ISettingsStoreFactory settings, AskHelmEdgeHandle edge, ILogger<AskHelmOpeners> logger)
    {
        _settings = settings;
        _store = settings.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId);
        _edge = edge;
        _logger = logger;
    }

    /// <summary>Applies everything now and follows the settings. Harmless to call often.</summary>
    public void Start()
    {
        if (!_following)
        {
            _following = true;
            _store.Changed += (_, _) => _main.Post(Apply);
        }
        _main.Post(Apply);
    }

    /// <summary>Quick Capture is off: no handle, no icon, no alarm (the widget says the tool is off).</summary>
    public void Stop() => _main.Post(Apply);

    /// <summary>The "Ways to open" page is shown, or the user came back from the phone's settings: look again.</summary>
    public void Refresh() => _main.Post(Apply);

    private void Apply()
    {
        var moduleOn = QuickCaptureModule.IsModuleEnabled(_settings, QuickCaptureModule.ModuleId);
        try
        {
            AskHelmLauncherIcon.Apply(_context, moduleOn && _store.Current.LauncherIcon);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not {Action} the Ask Helm icon", _store.Current.LauncherIcon ? "show" : "hide");
        }
        _edge.Update();
        if (moduleOn && _store.Current.EdgeHandle) AskHelmOpenAlarm.Set(_context);
        else AskHelmOpenAlarm.Cancel(_context);
        AskHelmWidgets.RefreshAll(_context);
    }
}

/// <summary>The hourly check that puts the edge handle back (RTC, never RTC_WAKEUP: it waits while the phone sleeps).</summary>
internal static class AskHelmOpenAlarm
{
    public const string ActionKeep = "com.huyhung1404.helm.askhelm.KEEP";
    private const int Request = 0x7A51;

    public static void Set(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var first = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)TimeSpan.FromHours(1).TotalMilliseconds;
        alarms.SetInexactRepeating(AlarmType.Rtc, first, AlarmManager.IntervalHour, Intent(context));
    }

    public static void Cancel(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms) alarms.Cancel(Intent(context));
    }

    private static PendingIntent Intent(Context context) =>
        PendingIntent.GetBroadcast(context, Request, new Intent(context, typeof(AskHelmOpenReceiver)).SetAction(ActionKeep)!,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
}

/// <summary>Puts the edge handle back after a reboot, an update of Helm, or Android stopping Helm (the hourly alarm).</summary>
[BroadcastReceiver(Exported = true)]
[Android.App.IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class AskHelmOpenReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced or AskHelmOpenAlarm.ActionKeep)) return;
        try
        {
            var services = HelmAndroidServices.Current;
            if (!QuickCaptureModule.IsModuleEnabled(services.GetRequiredService<ISettingsStoreFactory>(), QuickCaptureModule.ModuleId)) return;
            services.GetRequiredService<AskHelmOpeners>().Start();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Ask Helm's edge handle could not start: {ex}");
        }
    }
}
