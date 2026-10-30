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

namespace Helm.Modules.Habits;

/// <summary>
/// The skipped-habit reminder on Android. An inexact hourly alarm (cheap on the battery, kept across reboots by
/// <see cref="HabitReminderReceiver"/>) asks <see cref="HabitReminderService.TakeDue"/>, which decides whether today's
/// reminder is due; the app also checks whenever Habits is opened. Runs without the activity. The same pattern as
/// Missions' daily step reminder, with its own alarm, channel and notification.
/// </summary>
internal static class HabitReminders
{
    public const string ActionCheck = "com.huyhung1404.helm.habits.REMINDER_CHECK";
    private const string ChannelId = "habits_reminders";
    private const int NotificationId = 0x7C11;
    private const int AlarmRequestCode = 0x7C12;
    private const int PermissionRequestCode = 0x7C13;
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

    /// <summary>Shows today's reminder if one is due now (reminders on, Habits on, notifications allowed).</summary>
    public static void CheckNow(Context context)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            if (general.EnabledModules.TryGetValue(HabitsIds.ModuleId, out var on) && !on) return;
            // Blocked notifications: do not use up today's reminder; it comes once they are allowed.
            if (!CanNotify(context)) return;
            if (services.GetRequiredService<HabitReminderService>().TakeDue() is { } reminder) Post(context, reminder);
        }
        catch (Exception ex)
        {
            Log(ex, "Habits reminder check failed");
        }
    }

    public static void Post(Context context, HabitReminder reminder)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Skipped habit reminders", NotificationImportance.Default)
            {
                Description = "The habits skipped for two days or more in a row, once a day.",
            });
            var firstLine = reminder.Message.Split('\n')[0];
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(R.Drawable(context, "habits_notification"))
                .SetContentTitle(reminder.Title)
                .SetContentText(firstLine)
                .SetStyle(new Notification.BigTextStyle().BigText(reminder.Message))
                .SetAutoCancel(true)
                .SetNumber(reminder.Count);
            if (OpenHabitsIntent(context, AlarmRequestCode + 1) is { } open) builder.SetContentIntent(open);
            manager.Notify(NotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not post the Habits reminder");
        }
    }

    public static bool CanNotify(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return false;
        return context.GetSystemService(Context.NotificationService) is NotificationManager { } m && m.AreNotificationsEnabled();
    }

    /// <summary>Android 13+: asks for the notification permission once (another tool may have asked already).</summary>
    public static void AskPermissionOnce(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CanNotify(context)) return;
        var prefs = context.GetSharedPreferences("helm_habits_reminders", FileCreationMode.Private);
        if (prefs is null || prefs.GetBoolean(PermissionAskedKey, false) || ActivityHost.Current is not { } activity) return;
        prefs.Edit()?.PutBoolean(PermissionAskedKey, true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }

    /// <summary>Opens Helm on the Habits page (MainActivity reads the module extra).</summary>
    private static PendingIntent? OpenHabitsIntent(Context context, int requestCode)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, HabitsIds.ModuleId);
        return PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent AlarmIntent(Context context)
    {
        var intent = new Intent(context, typeof(HabitReminderReceiver)).SetAction(ActionCheck)!;
        return PendingIntent.GetBroadcast(context, AlarmRequestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Habits.Reminders").LogWarning(ex, message);
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
public sealed class HabitReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        switch (intent?.Action)
        {
            case Intent.ActionBootCompleted:
                HabitReminders.Schedule(context);
                HabitReminders.CheckNow(context);
                break;
            case HabitReminders.ActionCheck:
                HabitReminders.CheckNow(context);
                break;
        }
    }
}

/// <summary>Resource ids by name: looking them up at run time does not depend on how the app maps library ids.</summary>
internal static class R
{
    private static readonly Dictionary<string, int> s_cache = new(StringComparer.Ordinal);

    public static int Drawable(Context context, string name) => Get(context, name, "drawable");

    public static int Get(Context context, string name, string type)
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
