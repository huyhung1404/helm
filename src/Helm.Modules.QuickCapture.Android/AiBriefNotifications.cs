using Android.Content;
using Android.Content.PM;
using Helm.Ai.Auto;
using Helm.Core;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

[assembly: Android.App.UsesPermission("android.permission.POST_NOTIFICATIONS")]

namespace Helm.Modules.QuickCapture;

/// <summary>
/// "Bản tin sáng · Thứ Ba 03/11": a notification for each brief a Helm PC wrote by itself (<see cref="AiBriefAlerts"/>
/// decides which are new), as background sync brings it. One per brief; a tap opens Helm. Ask Helm reads them out
/// ("bản tin sáng nay nói gì?").
/// </summary>
public static class AiBriefNotifications
{
    private const string ChannelId = "ai_briefs";

    /// <summary>From now on every new brief is shown (also those that arrived while Helm was not running).</summary>
    public static void Start(IServiceProvider services, Context context) =>
        services.GetRequiredService<AiBriefAlerts>().Start(brief => Post(context, brief));

    public static void Post(Context context, SyncedItem<AiBrief> brief)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return;
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager || !manager.AreNotificationsEnabled()) return;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Briefs from your PC", NotificationImportance.Default)
            {
                Description = "The morning brief, the evening review and the other jobs your Helm PC runs by itself.",
            });
            var id = NotificationId(brief.Id);
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(Resource.Drawable.ask_helm_tile)
                .SetContentTitle(brief.Value.Title)
                .SetContentText(brief.Value.Text)
                .SetStyle(new Notification.BigTextStyle().BigText(brief.Value.Text))
                .SetAutoCancel(true)
                .SetWhen(brief.Value.CreatedAt.ToUnixTimeMilliseconds())
                .SetShowWhen(true);
            var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
            if (launch is not null)
            {
                launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                builder.SetContentIntent(PendingIntent.GetActivity(context, id, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
            }
            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            try
            {
                HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("AiBriefs").LogWarning(ex, "Could not show a brief");
            }
            catch (Exception)
            {
                Android.Util.Log.Warn("Helm", $"Could not show a brief: {ex}");
            }
        }
    }

    /// <summary>The same id for the same brief in any process (string hashes change per process).</summary>
    private static int NotificationId(string briefId)
    {
        var hash = 2166136261u;
        foreach (var c in briefId) hash = (hash ^ c) * 16777619u;
        return 0x4B000000 | (int)(hash & 0xFFFFF);
    }
}
