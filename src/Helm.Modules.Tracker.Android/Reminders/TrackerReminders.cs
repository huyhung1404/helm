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

namespace Helm.Modules.Tracker;

/// <summary>
/// Due-date reminders on Android. An inexact hourly alarm (cheap on the battery, kept across reboots by
/// <see cref="TrackerReminderReceiver"/>) asks <see cref="TrackerReminderService.TakeDue"/>, which decides whether
/// today's reminder is due; the app also checks whenever Tracker starts. Runs without the activity.
/// </summary>
internal static class TrackerReminders
{
    public const string ActionCheck = "com.huyhung1404.helm.tracker.REMINDER_CHECK";
    private const string ChannelId = "tracker_reminders";
    private const int NotificationId = 0x7A11;
    private const int AlarmRequestCode = 0x7A12;
    private const int PermissionRequestCode = 0x7A13;
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

    /// <summary>Shows today's reminder if one is due now (reminders on, Tracker on, notifications allowed).</summary>
    public static void CheckNow(Context context)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            if (general.EnabledModules.TryGetValue(TrackerIds.ModuleId, out var on) && !on) return;
            // Blocked notifications: do not use up today's reminder; it comes once they are allowed.
            if (!CanNotify(context)) return;
            if (services.GetRequiredService<TrackerReminderService>().TakeDue() is { } reminder) Post(context, reminder);
        }
        catch (Exception ex)
        {
            Log(ex, "Tracker reminder check failed");
        }
    }

    public static void Post(Context context, TrackerReminder reminder)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Due date reminders", NotificationImportance.Default)
            {
                Description = "Tasks and debts that are due soon or overdue, once a day.",
            });
            var firstLine = reminder.Message.Split('\n')[0];
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(R.Drawable(context, "tracker_notification"))
                .SetContentTitle(reminder.Title)
                .SetContentText(firstLine)
                .SetStyle(new Notification.BigTextStyle().BigText(reminder.Message))
                .SetAutoCancel(true)
                .SetNumber(reminder.Total);
            if (TrackerWidgets.OpenAppIntent(context, AlarmRequestCode + 1) is { } open) builder.SetContentIntent(open);
            manager.Notify(NotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not post the Tracker reminder");
        }
    }

    /// <summary>Whether Helm may post notifications (Android 13+ asks; any version lets the user turn them off).</summary>
    public static bool CanNotify(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return false;
        return context.GetSystemService(Context.NotificationService) is NotificationManager { } m && m.AreNotificationsEnabled();
    }

    /// <summary>Android 13+: asks once for the notification permission, from the visible activity.</summary>
    public static void AskPermissionOnce(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CanNotify(context)) return;
        var prefs = context.GetSharedPreferences("helm_tracker_reminders", FileCreationMode.Private);
        if (prefs is null || prefs.GetBoolean(PermissionAskedKey, false) || ActivityHost.Current is not { } activity) return;
        prefs.Edit()?.PutBoolean(PermissionAskedKey, true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }

    private static PendingIntent AlarmIntent(Context context)
    {
        var intent = new Intent(context, typeof(TrackerReminderReceiver)).SetAction(ActionCheck)!;
        return PendingIntent.GetBroadcast(context, AlarmRequestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static void Log(Exception ex, string message)
    {
        try { HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Tracker.Reminders").LogWarning(ex, message); }
        catch (Exception) { Android.Util.Log.Warn("Helm", $"{message}: {ex}"); }
    }
}

/// <summary>The hourly reminder alarm, and the reboot that clears alarms (the check is set again).</summary>
[BroadcastReceiver(Exported = true)]
[Android.App.IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class TrackerReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        switch (intent?.Action)
        {
            case Intent.ActionBootCompleted:
                TrackerReminders.Schedule(context);
                TrackerReminders.CheckNow(context);
                break;
            case TrackerReminders.ActionCheck:
                TrackerReminders.CheckNow(context);
                break;
        }
    }
}
