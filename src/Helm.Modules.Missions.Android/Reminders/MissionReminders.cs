using Android.Content;
using Android.Content.PM;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlarmManager = Android.App.AlarmManager;
using AlarmType = Android.App.AlarmType;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

[assembly: Android.App.UsesPermission("android.permission.POST_NOTIFICATIONS")]
[assembly: Android.App.UsesPermission("android.permission.RECEIVE_BOOT_COMPLETED")]

namespace Helm.Modules.Missions;

/// <summary>
/// The daily step reminder on Android. An inexact hourly alarm (cheap on the battery, kept across reboots by
/// <see cref="MissionReminderReceiver"/>) asks <see cref="MissionReminderService.TakeDue"/>, which decides whether
/// today's reminder is due; the app also checks whenever Missions is opened. Runs without the activity. The same
/// pattern as Tracker's due-date reminders, with its own alarm, channel and notification.
/// </summary>
internal static class MissionReminders
{
    public const string ActionCheck = "com.huyhung1404.helm.missions.REMINDER_CHECK";
    private const string ChannelId = "missions_reminders";
    private const int NotificationId = 0x7B11;
    private const int AlarmRequestCode = 0x7B12;
    private const int PermissionRequestCode = 0x7B13;
    private const string PermissionAskedKey = "notification_permission_asked";

    /// <summary>Sets the hourly check (replacing any earlier one). Harmless to call often.</summary>
    public static void Schedule(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var first = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        alarms.SetInexactRepeating(AlarmType.Rtc, first, AlarmManager.IntervalHour, AlarmIntent(context));
    }

    public static void Cancel(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms) alarms.Cancel(AlarmIntent(context));
    }

    /// <summary>Shows today's reminder if one is due now (reminders on, Missions on, notifications allowed).</summary>
    public static void CheckNow(Context context)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            if (general.EnabledModules.TryGetValue(MissionsIds.ModuleId, out var on) && !on) return;
            // Blocked notifications: do not use up today's reminder; it comes once they are allowed.
            if (!CanNotify(context)) return;
            if (services.GetRequiredService<MissionReminderService>().TakeDue() is { } reminder) Post(context, reminder);
        }
        catch (Exception ex)
        {
            Log(ex, "Missions reminder check failed");
        }
    }

    public static void Post(Context context, MissionReminder reminder)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Daily step reminders", NotificationImportance.Default)
            {
                Description = "The step each mission in progress is on, once a day.",
            });
            var firstLine = reminder.Message.Split('\n')[0];
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(R.Drawable(context, "missions_notification"))
                .SetContentTitle(reminder.Title)
                .SetContentText(firstLine)
                .SetStyle(new Notification.BigTextStyle().BigText(reminder.Message))
                .SetAutoCancel(true)
                .SetNumber(reminder.Count);
            if (OpenMissionsIntent(context, AlarmRequestCode + 1) is { } open) builder.SetContentIntent(open);
            manager.Notify(NotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not post the Missions reminder");
        }
    }

    public static bool CanNotify(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return false;
        return context.GetSystemService(Context.NotificationService) is NotificationManager { } m && m.AreNotificationsEnabled();
    }

    /// <summary>Android 13+: asks for the notification permission once (Tracker may have asked already).</summary>
    public static void AskPermissionOnce(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CanNotify(context)) return;
        var prefs = context.GetSharedPreferences("helm_missions_reminders", FileCreationMode.Private);
        if (prefs is null || prefs.GetBoolean(PermissionAskedKey, false) || ActivityHost.Current is not { } activity) return;
        prefs.Edit()?.PutBoolean(PermissionAskedKey, true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }

    /// <summary>Opens Helm on the Missions page (MainActivity reads the module extra).</summary>
    private static PendingIntent? OpenMissionsIntent(Context context, int requestCode)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, MissionsIds.ModuleId);
        return PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent AlarmIntent(Context context)
    {
        var intent = new Intent(context, typeof(MissionReminderReceiver)).SetAction(ActionCheck)!;
        return PendingIntent.GetBroadcast(context, AlarmRequestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Missions.Reminders").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}

/// <summary>The hourly alarm, and the restart after a reboot (alarms do not survive one).</summary>
[BroadcastReceiver(Exported = true)]
[Android.App.IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class MissionReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        switch (intent?.Action)
        {
            case Intent.ActionBootCompleted:
                MissionReminders.Schedule(context);
                MissionReminders.CheckNow(context);
                break;
            case MissionReminders.ActionCheck:
                MissionReminders.CheckNow(context);
                break;
        }
    }
}

/// <summary>Resource ids by name: looking them up at run time does not depend on how the app maps library ids.</summary>
internal static class R
{
    private static readonly Dictionary<string, int> s_cache = new(StringComparer.Ordinal);

    public static int Drawable(Context context, string name) => Get(context, name, "drawable");

    private static int Get(Context context, string name, string type)
    {
        var key = type + "/" + name;
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var id)) return id;
            id = context.Resources?.GetIdentifier(name, type, context.PackageName) ?? 0;
            if (id == 0) throw new InvalidOperationException($"Missing Android resource {key}.");
            s_cache[key] = id;
            return id;
        }
    }
}
