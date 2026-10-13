using Android.Content;
using Android.Content.PM;
using Android.OS;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using Service = Android.App.Service;
using StartCommandFlags = Android.App.StartCommandFlags;
using StartCommandResult = Android.App.StartCommandResult;
using StopForegroundFlags = Android.App.StopForegroundFlags;

[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE_SPECIAL_USE")]
[assembly: Android.App.UsesPermission("android.permission.POST_NOTIFICATIONS")]

namespace Helm.Modules.Workspace;

/// <summary>
/// Keeps the phone watching your PCs while Helm is closed: a foreground service (its notification says so and has
/// Stop) that keeps Helm Sync's live channel open, and notifies when a Claude session finishes or waits for you
/// (permission, question), one group per VS Code window. Tapping a notification opens that conversation. Listed under
/// General → Running in background. Started and stopped by <see cref="WorkspaceModule"/>.
/// </summary>
[Android.App.Service(Name = "com.huyhung1404.helm.workspace.Watch", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class WorkspaceWatchService : Service
{
    public const string ExtraMachine = "com.huyhung1404.helm.extra.WORKSPACE_MACHINE";
    public const string ExtraSession = "com.huyhung1404.helm.extra.WORKSPACE_SESSION";
    private const string ActionStop = "com.huyhung1404.helm.workspace.STOP";
    private const string WatchChannel = "workspace_watch";
    private const string SessionsChannel = "workspace_sessions";
    private const int WatchNotificationId = 0x5701;
    private const int SessionNotificationId = 0x5702;

    private ILogger? _logger;
    private IDisposable? _live;
    private BackgroundActivity? _activity;
    private ISyncedCollection<WorkspaceSessionRecord>? _sessions;
    private ISyncedCollection<WorkspaceHostRecord>? _hosts;
    private WorkspaceRemoteViewModel? _viewModel;
    private readonly WorkspaceNotifier _notifier = new();
    private readonly object _gate = new();
    private bool _foreground;

    public static bool IsRunning { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
        var services = HelmAndroidServices.Current;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("Workspace.Watch");
        if (GetSystemService(NotificationService) is NotificationManager manager)
        {
            manager.CreateNotificationChannel(new NotificationChannel(WatchChannel, "Watching your PCs", NotificationImportance.Low)
            {
                Description = "Shown while the phone watches your PCs' Claude sessions in the background.",
            });
            manager.CreateNotificationChannel(new NotificationChannel(SessionsChannel, "Claude sessions", NotificationImportance.High)
            {
                Description = "A Claude session on a PC finished, or waits for your permission or answer.",
            });
        }

        // Live sync in the background too (MainActivity reads the demand when Helm goes to the background).
        _live = services.GetRequiredService<LiveSyncDemand>().Hold();
        var engine = services.GetService<SyncEngine>();
        engine?.SetPollInterval(SyncEngine.ForegroundPollInterval);
        engine?.SetLive(true);

        _sessions = services.GetRequiredService<ISyncedCollection<WorkspaceSessionRecord>>();
        _hosts = services.GetRequiredService<ISyncedCollection<WorkspaceHostRecord>>();
        _viewModel = services.GetRequiredService<WorkspaceRemoteViewModel>();
        _sessions.Changed += OnChanged;
        _hosts.Changed += OnChanged;
        Check();

        _activity = services.GetRequiredService<IBackgroundActivities>().Start(WorkspaceIds.ModuleId, "Workspace Controller", Detail(), () =>
        {
            _viewModel?.Settings.Update(s => s.RunInBackground = false);
            return Task.CompletedTask;
        });
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            // The notification's Stop: no more watching in the background (the tool itself stays on).
            _viewModel?.Settings.Update(s => s.RunInBackground = false);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (!_foreground)
        {
            var notification = WatchNotification();
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(WatchNotificationId, notification, ForegroundService.TypeSpecialUse);
            else StartForeground(WatchNotificationId, notification);
            _foreground = true;
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        if (_sessions is not null) _sessions.Changed -= OnChanged;
        if (_hosts is not null) _hosts.Changed -= OnChanged;
        _activity?.Dispose();
        _live?.Dispose();
        // Back to the battery-saving background unless Helm is in front.
        if (ActivityHost.Current is null)
        {
            var engine = HelmAndroidServices.Current.GetService<SyncEngine>();
            engine?.SetPollInterval(SyncEngine.PhoneBackgroundPollInterval);
            engine?.SetLive(false);
        }
        if (_foreground) StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    /// <summary>Starts watching (from the module). Android may refuse a start from the background; it is tried again on resume.</summary>
    public static bool Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(WorkspaceWatchService));
            context.StartForegroundService(intent);
            return true;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Workspace.Watch")
                .LogWarning(ex, "Workspace Controller could not start watching in the background yet");
            return false;
        }
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(WorkspaceWatchService)));

    /// <summary>The conversation is open: its notification goes away, and its window's summary when nothing is left in it.</summary>
    public static void Cancel(Context context, string sessionId)
    {
        if (context.GetSystemService(NotificationService) is not NotificationManager manager) return;
        var active = manager.GetActiveNotifications() ?? [];
        var group = active.FirstOrDefault(n => n.Tag == sessionId && n.Id == SessionNotificationId)?.Notification?.Group;
        manager.Cancel(sessionId, SessionNotificationId);
        if (group is null) return;
        var others = active.Count(n => n.Id == SessionNotificationId && n.Tag != sessionId && n.Tag != group && n.Notification?.Group == group);
        if (others == 0) manager.Cancel(group, SessionNotificationId);
    }

    private void OnChanged(object? sender, SyncedChangedEventArgs e)
    {
        try
        {
            Check();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Workspace Controller could not check for news");
        }
    }

    private void Check()
    {
        if (_sessions is null || _hosts is null) return;
        IReadOnlyList<WorkspaceNotice> notices;
        lock (_gate)
        {
            // The conversation you look at in Helm needs no notification.
            var viewing = ActivityHost.Current is not null ? _viewModel?.OpenSessionId : null;
            notices = _notifier.Next(_sessions.All().Select(i => i.Value).ToList(), _hosts.All().Select(i => i.Value).ToList(), viewing);
        }
        if (_activity is not null) _activity.Detail = Detail();
        if (notices.Count == 0 || GetSystemService(NotificationService) is not NotificationManager manager) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return;
        foreach (var notice in notices)
        {
            var builder = new Notification.Builder(this, SessionsChannel)
                .SetSmallIcon(R.Drawable(this, "workspace_notification"))
                .SetContentTitle(notice.Title)
                .SetContentText(notice.Text)
                .SetStyle(new Notification.BigTextStyle().BigText(notice.Text))
                .SetSubText(notice.GroupTitle)
                .SetGroup(notice.Group)
                .SetAutoCancel(true)
                .SetCategory(notice.Kind == NoticeKind.Done ? Notification.CategoryStatus : Notification.CategoryReminder);
            if (OpenIntent(notice.MachineId, notice.SessionId) is { } open) builder.SetContentIntent(open);
            manager.Notify(notice.SessionId, SessionNotificationId, builder.Build());

            // A summary per window, so a busy window folds into one group.
            var summary = new Notification.Builder(this, SessionsChannel)
                .SetSmallIcon(R.Drawable(this, "workspace_notification"))
                .SetContentTitle(notice.GroupTitle)
                .SetGroup(notice.Group)
                .SetGroupSummary(true)
                .SetAutoCancel(true);
            manager.Notify(notice.Group, SessionNotificationId, summary.Build());
        }
    }

    private string Detail()
    {
        var now = DateTimeOffset.Now;
        var online = _hosts?.All().Count(h => h.Value.IsOnline(now)) ?? 0;
        return online == 1 ? "Watching 1 PC" : $"Watching {online} PCs";
    }

    private Notification WatchNotification()
    {
        var stop = PendingIntent.GetService(this, 0x5710, new Intent(this, typeof(WorkspaceWatchService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = new Notification.Builder(this, WatchChannel)
            .SetSmallIcon(R.Drawable(this, "workspace_notification"))
            .SetContentTitle("Workspace Controller")
            .SetContentText("Watching your PCs for Claude. Tap to open.")
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build());
        if (OpenIntent(null, null) is { } open) builder.SetContentIntent(open);
        return builder.Build()!;
    }

    private PendingIntent? OpenIntent(string? machineId, string? sessionId)
    {
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, WorkspaceIds.ModuleId);
        if (sessionId is not null)
        {
            launch.PutExtra(ExtraMachine, machineId);
            launch.PutExtra(ExtraSession, sessionId);
        }
        var code = sessionId is null ? 0x5711 : 0x5800 + (sessionId.GetHashCode() & 0x3FFF);
        return PendingIntent.GetActivity(this, code, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}

/// <summary>Resource ids by name: looking them up at run time does not depend on how the app maps library ids.</summary>
internal static class R
{
    private static readonly Dictionary<string, int> s_cache = new(StringComparer.Ordinal);

    public static int Drawable(Context context, string name)
    {
        lock (s_cache)
        {
            if (s_cache.TryGetValue(name, out var id)) return id;
            id = context.Resources?.GetIdentifier(name, "drawable", context.PackageName) ?? 0;
            s_cache[name] = id;
            return id;
        }
    }
}
