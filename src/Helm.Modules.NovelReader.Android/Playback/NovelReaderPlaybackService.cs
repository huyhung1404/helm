using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Helm.Core;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using NotificationVisibility = Android.App.NotificationVisibility;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using Service = Android.App.Service;
using StartCommandFlags = Android.App.StartCommandFlags;
using StartCommandResult = Android.App.StartCommandResult;
using StopForegroundFlags = Android.App.StopForegroundFlags;

[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK")]
[assembly: Android.App.UsesPermission("android.permission.WAKE_LOCK")]

namespace Helm.Modules.NovelReader.Playback;

/// <summary>
/// Keeps Novel Reader reading aloud while Helm is in the background or the screen is off, and goes on to the next
/// chapter by itself: a foreground service of type media playback, with its media notification (the novel, the chapter,
/// the cover; previous, play/pause, next, stop), a MediaSession for the lock screen, headsets, Bluetooth and watches,
/// a partial wake lock while reading, and a pause when the headphones are unplugged. Reading itself is
/// ReadAloudController in the shared core; this only keeps the process alive and passes the buttons on
/// (<see cref="AndroidAudioOutput.MediaButton"/>). It runs from the first sentence to the moment reading stops; while
/// paused the notification stays, so reading goes on from the lock screen. Media notifications do not need the
/// notification permission.
/// </summary>
[Android.App.Service(Name = "com.huyhung1404.helm.novel.Playback", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class NovelReaderPlaybackService : Service
{
    public const string ActionShow = "com.huyhung1404.helm.novel.SHOW";
    private const string ActionPlayPause = "com.huyhung1404.helm.novel.PLAY_PAUSE";
    private const string ActionPrevious = "com.huyhung1404.helm.novel.PREVIOUS";
    private const string ActionNext = "com.huyhung1404.helm.novel.NEXT";
    private const string ActionStop = "com.huyhung1404.helm.novel.STOP";
    private const string CustomStop = "stop";
    private const string ChannelId = "novel_reader_playback";
    private const int NotificationId = 0x6E01;

    /// <summary>How long the phone stays awake after a sentence starts if nothing else happens (a stuck voice).</summary>
    private static readonly TimeSpan AwakeFor = TimeSpan.FromMinutes(10);

    private static NovelReaderPlaybackService? s_current;

    private AndroidAudioOutput? _output;
    private ILogger? _logger;
    private MediaSession? _session;
    private PowerManager.WakeLock? _wakeLock;
    private NoisyReceiver? _noisy;
    private byte[]? _artworkSource;
    private Bitmap? _artwork;
    private bool _foreground;

    /// <summary>The service is running (created and not destroyed).</summary>
    public static bool IsRunning => s_current is not null;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        s_current = this;
        var services = HelmAndroidServices.Current;
        _output = services.GetRequiredService<AndroidAudioOutput>();
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("NovelReader.Playback");
        _output.Changed += OnOutputChanged;

        if (GetSystemService(NotificationService) is NotificationManager manager)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Reading aloud", NotificationImportance.Low)
            {
                Description = "The novel being read aloud, with play, pause and skip.",
            });
        }

        _session = new MediaSession(this, "HelmNovelReader");
        _session.SetCallback(new SessionCallback(this));
        if (OpenHelmIntent() is { } open) _session.SetSessionActivity(open);
        _session.Active = true;

        if (GetSystemService(PowerService) is PowerManager power)
        {
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Helm:NovelReader");
            _wakeLock?.SetReferenceCounted(false);
        }

        _noisy = new NoisyReceiver(this);
        var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterReceiver(_noisy, filter, ReceiverFlags.NotExported);
        else RegisterReceiver(_noisy, filter);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Android requires the notification soon after the start, even when there is nothing to read any more.
        Refresh();
        _output?.ServiceStarted();
        switch (intent?.Action)
        {
            case ActionPlayPause: Press(MediaButton.PlayPause); break;
            case ActionPrevious: Press(MediaButton.Previous); break;
            case ActionNext: Press(MediaButton.Next); break;
            case ActionStop: Press(MediaButton.Stop); break;
        }
        if (_output?.Current is null) Finish();
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_output is not null) _output.Changed -= OnOutputChanged;
        if (_noisy is not null)
        {
            try
            {
                UnregisterReceiver(_noisy);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }
        }
        _session?.Release();
        _session = null;
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        _artwork?.Recycle();
        _artwork = null;
        s_current = null;
        base.OnDestroy();
    }

    /// <summary>
    /// The user swiped Helm away from the recent apps: Android may stop the app, so the place reading stopped is saved
    /// first. Reading goes on while the process lives.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        try
        {
            HelmAndroidServices.Current.GetService<NovelReaderViewModel>()?.FlushProgress();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save the reading position");
        }
        base.OnTaskRemoved(rootIntent);
    }

    private void Press(MediaButton button) => _output?.Press(button);

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        if (_output?.Current is null)
        {
            Finish();
            return;
        }
        if (ReferenceEquals(e, AndroidAudioOutput.KeepAwake)) StayAwake();
        else Refresh();
    }

    /// <summary>Shows or updates the notification and the session, and holds the phone awake while reading.</summary>
    private void Refresh()
    {
        var now = _output?.Current ?? new NowPlaying("Novel Reader", "", true, null);
        try
        {
            UpdateSession(now);
            var notification = BuildNotification(now);
            if (!_foreground)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
                else StartForeground(NotificationId, notification);
                _foreground = true;
            }
            else if (GetSystemService(NotificationService) is NotificationManager manager)
            {
                manager.Notify(NotificationId, notification);
            }
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            _logger?.LogWarning(ex, "Could not show the reading-aloud notification");
        }
        if (now.IsPaused) LetSleep();
        else StayAwake();
    }

    private void Finish()
    {
        LetSleep();
        if (_session is not null)
        {
            _session.SetPlaybackState(new PlaybackState.Builder().SetState(PlaybackStateCode.Stopped, PlaybackState.PlaybackPositionUnknown, 0)!.Build());
            _session.Active = false;
        }
        StopForeground(StopForegroundFlags.Remove);
        _foreground = false;
        StopSelf();
    }

    private void StayAwake()
    {
        try
        {
            _wakeLock?.Acquire((long)AwakeFor.TotalMilliseconds);
        }
        catch (Java.Lang.Exception ex)
        {
            _logger?.LogWarning(ex, "Could not keep the phone awake for reading aloud");
        }
    }

    private void LetSleep()
    {
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
    }

    // ---- The notification and the session ----------------------------------------------------------------------

    private Notification BuildNotification(NowPlaying now)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(R.Drawable(this, "novel_notification"))!
            .SetContentTitle(now.Title)!
            .SetContentText(now.Subtitle)!
            .SetVisibility(NotificationVisibility.Public)!
            .SetCategory(Notification.CategoryTransport)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetOngoing(!now.IsPaused)!
            // Swiping away a paused notification stops reading.
            .SetDeleteIntent(ServiceIntent(ActionStop, 4))!
            .AddAction(Action("novel_previous", "Previous paragraph", ActionPrevious, 1))!
            .AddAction(now.IsPaused ? Action("novel_play", "Read aloud", ActionPlayPause, 2) : Action("novel_pause", "Pause", ActionPlayPause, 2))!
            .AddAction(Action("novel_next", "Next paragraph", ActionNext, 3))!
            .AddAction(Action("novel_stop", "Stop", ActionStop, 4))!
            .SetStyle(new Notification.MediaStyle().SetMediaSession(_session?.SessionToken)!.SetShowActionsInCompactView(0, 1, 2))!;
        if (Artwork(now) is { } artwork) builder.SetLargeIcon(artwork);
        if (OpenHelmIntent() is { } open) builder.SetContentIntent(open);
        return builder.Build()!;
    }

    /// <summary>The lock screen, headsets and Android 13+ media controls read the session.</summary>
    private void UpdateSession(NowPlaying now)
    {
        if (_session is not { } session) return;
        var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, now.Title)!
            .PutString(MediaMetadata.MetadataKeyArtist, now.Subtitle)!
            .PutString(MediaMetadata.MetadataKeyAlbum, NovelReaderIds.DisplayName)!;
        if (Artwork(now) is { } artwork) metadata.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, artwork);
        session.SetMetadata(metadata.Build());
        var state = new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause
                | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionStop)!
            .SetState(now.IsPaused ? PlaybackStateCode.Paused : PlaybackStateCode.Playing, PlaybackState.PlaybackPositionUnknown, now.IsPaused ? 0 : 1)!
            // Android 13+ draws the media controls from the session: the stop button is a custom action there.
            .AddCustomAction(new PlaybackState.CustomAction.Builder(CustomStop, "Stop", R.Drawable(this, "novel_stop")).Build())!;
        session.SetPlaybackState(state.Build());
        if (!session.Active) session.Active = true;
    }

    private Notification.Action Action(string icon, string title, string action, int requestCode) =>
        new Notification.Action.Builder(Icon.CreateWithResource(this, R.Drawable(this, icon)), title, ServiceIntent(action, requestCode)).Build()!;

    private PendingIntent ServiceIntent(string action, int requestCode)
    {
        var intent = new Intent(this, typeof(NovelReaderPlaybackService)).SetAction(action)!;
        return PendingIntent.GetService(this, 0x6E10 + requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    /// <summary>Opens Helm on Novel Reader (MainActivity reads the module extra).</summary>
    private PendingIntent? OpenHelmIntent()
    {
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, NovelReaderIds.ModuleId);
        return PendingIntent.GetActivity(this, 0x6E20, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    /// <summary>The cover, decoded once per novel and kept small (the notification's large icon).</summary>
    private Bitmap? Artwork(NowPlaying now)
    {
        if (ReferenceEquals(now.Artwork, _artworkSource)) return _artwork;
        _artworkSource = now.Artwork;
        _artwork?.Recycle();
        _artwork = null;
        if (now.Artwork is not { Length: > 0 } bytes) return null;
        try
        {
            _artwork = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, new BitmapFactory.Options { InSampleSize = 2 });
        }
        catch (Java.Lang.Exception ex)
        {
            _logger?.LogDebug(ex, "Could not decode the cover for the notification");
        }
        return _artwork;
    }

    /// <summary>The lock screen, headsets (one press: play or pause), Bluetooth and watches.</summary>
    private sealed class SessionCallback(NovelReaderPlaybackService service) : MediaSession.Callback
    {
        public override void OnPlay() => service.Press(MediaButton.Play);

        public override void OnPause() => service.Press(MediaButton.Pause);

        public override void OnSkipToNext() => service.Press(MediaButton.Next);

        public override void OnSkipToPrevious() => service.Press(MediaButton.Previous);

        public override void OnStop() => service.Press(MediaButton.Stop);

        public override void OnCustomAction(string action, Bundle? extras)
        {
            if (action == CustomStop) service.Press(MediaButton.Stop);
        }
    }

    /// <summary>Headphones unplugged or Bluetooth disconnected: pause before the phone's speaker reads out loud.</summary>
    private sealed class NoisyReceiver(NovelReaderPlaybackService service) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) service.Press(MediaButton.Pause);
        }
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
            if (id == 0) throw new InvalidOperationException($"Missing Android resource drawable/{name}.");
            s_cache[name] = id;
            return id;
        }
    }
}
