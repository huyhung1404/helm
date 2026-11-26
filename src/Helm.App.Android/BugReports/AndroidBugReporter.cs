using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.Media;
using Android.OS;
using Android.Provider;
using FluentIcons.Common;
using Helm.BugReports.Ui;
using Helm.App.Android.ViewModels;
using Helm.Core;
using Helm.Core.Diagnostics;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Helm.Core.Services;
using Helm.Shell.BugReports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

[assembly: Android.App.UsesPermission("android.permission.VIBRATE")]

namespace Helm.App.Android.BugReports;

/// <summary>
/// Bug reports on the phone: the report page from anywhere (the app bar's bug button, shaking the phone while Helm is
/// open, the Quick Settings tile), the black box's hooks, the report a crash left, notifications when an agent asks or a
/// fix arrives, and pictures from the gallery, the camera or the clipboard (Helm never takes screenshots). It is also what
/// the shared Bug reports page and report form ask of the phone (<see cref="IBugReportsPageHost"/>,
/// <see cref="IBugReportFormHost"/>).
/// </summary>
public sealed class AndroidBugReporter(
    IServiceProvider services,
    BugStore store,
    BlackBox box,
    ShellNavigator navigator,
    IModuleHost<IAndroidModule> modules,
    ISettingsStoreFactory settings,
    HelmPaths paths,
    IUiDispatcher ui,
    ILogger<AndroidBugReporter> logger) : IBugReportsPageHost, IBugReportFormHost
{
    /// <summary>String extra of an intent that opens Helm: <see cref="ReportRequest"/>, <see cref="CrashRequest"/>, or a bug's id.</summary>
    public const string ExtraBug = "com.huyhung1404.helm.extra.BUG";
    public const string ReportRequest = "report";
    public const string CrashRequest = "crash";
    public const string ReportKey = "bug-report";
    public const string PageKey = "bugs";

    private const string ChannelId = "bug_reports";
    private const string CameraFolder = "bug-camera";
    private const int MaxSide = 2000;

    private static readonly object s_gate = new();
    private static string? s_requested;

    private readonly HashSet<string> _notifiedQuestions = [];
    private readonly HashSet<string> _notifiedFixes = [];
    private readonly ISettingsStore<BugReportSettings> _settings = settings.Get<BugReportSettings>(BugReportSettings.StoreId);
    private BlackBoxSnapshot? _crash;
    private ShakeListener? _shake;
    private int _checkQueued;
    private bool _started;

    public ISettingsStore<BugReportSettings> Settings => _settings;

    /// <summary>An intent asked for the report page or a bug (the activity calls it; handled once the shell runs).</summary>
    public static void Request(string what)
    {
        lock (s_gate) s_requested = what;
    }

    /// <summary>Once, when the shell exists (UI thread).</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        AndroidBlackBoxHooks.Install(box, navigator);
        BugReportsServices.StartBlackBox(services, () => navigator.Title);
        foreach (var bug in store.All().Where(b => b.Value.PendingQuestion is not null)) _notifiedQuestions.Add(bug.Value.PendingQuestion!);
        store.Changed += (_, _) => QueueCheck();
        // Leaving the report page lets the black box record again (the report keeps the moment it froze).
        navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellNavigator.ActiveTab) && navigator.ActiveTab?.Key != ReportKey
                && navigator.Tabs.FirstOrDefault(t => t.Key == ReportKey)?.Page is BugReportFormViewModel form) form.Close();
        };
        _crash = BlackBox.TakeCrash(paths.CrashBlackBoxFile);
        if (_crash is not null) Notify("Helm closed unexpectedly", "Its black box was kept. Tap to report the crash.", CrashRequest, 1);
        ActivityHost.Resumed += (_, _) => { StartShake(); OpenRequested(); };
        ActivityHost.Paused += (_, _) => StopShake();
        _settings.Changed += (_, _) => ui.Post(() => { StopShake(); if (ActivityHost.Current is not null) StartShake(); });
        if (ActivityHost.Current is not null) StartShake();
        Check();
        OpenRequested();
    }

    /// <summary>Shows what an intent asked for (a report, the crash report, a bug).</summary>
    public void OpenRequested()
    {
        string? what;
        lock (s_gate)
        {
            what = s_requested;
            s_requested = null;
        }
        switch (what)
        {
            case null:
                return;
            case ReportRequest:
                Report();
                break;
            case CrashRequest when _crash is { } crash:
                Report(crash, fromCrash: true);
                break;
            case CrashRequest:
                ShowPage();
                break;
            default:
                ShowPage(what);
                break;
        }
    }

    /// <summary>Freezes the black box and shows the report page (the open one when there is one).</summary>
    public void Report(BlackBoxSnapshot? crash = null, bool fromCrash = false)
    {
        if (navigator.Tabs.FirstOrDefault(t => t.Key == ReportKey) is { Page: BugReportFormViewModel { IsDone: false } } open)
        {
            navigator.Activate(open);
            return;
        }
        if (navigator.Tabs.FirstOrDefault(t => t.Key == ReportKey) is { } done) navigator.Close(done);
        AndroidBlackBoxHooks.Flush();
        var page = navigator.Title;
        var frozen = box.Freeze();
        if (fromCrash) _crash = null;
        var tools = modules.Modules.Select(m => m.DisplayName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var tool = fromCrash ? null : tools.FirstOrDefault(t => page.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        var form = new BugReportFormViewModel(store, box, crash ?? frozen, tool, tools, fromCrash);
        navigator.Open(new ShellTab(ReportKey, form, "Report a bug", Symbol.Bug, null, null));
    }

    /// <summary>Leaves the report page (sent or cancelled).</summary>
    public void CloseReport()
    {
        if (navigator.Tabs.FirstOrDefault(t => t.Key == ReportKey) is not { } tab) return;
        (tab.Page as BugReportFormViewModel)?.Close();
        navigator.Close(tab);
    }

    /// <summary>The Bug reports page, with <paramref name="id"/> opened.</summary>
    public void ShowPage(string? id = null)
    {
        if (navigator.Describe(PageKey) is { } tab) navigator.Open(tab);
        if (id is not null) services.GetRequiredService<BugReportsViewModel>().Open(id);
    }

    // ---- what the shared page and form ask of the phone ----

    void IBugReportsPageHost.Report() => Report();

    string IBugReportsPageHost.ReportHint => "shake the phone, or use the Quick Settings tile";

    ISettingsStore<BugReportSettings>? IBugReportsPageHost.ShakeSettings => _settings;

    /// <summary>The page has no app bar on a phone: its menu button opens the drawer.</summary>
    Action<Avalonia.Controls.Control>? IBugReportsPageHost.OpenMenu => Helm.Core.Ui.ShellChrome.OpenNavigation;

    void IBugReportFormHost.Close() => CloseReport();

    void IBugReportFormHost.OpenBug(string id) => ShowPage(id);

    public bool CanTakePhoto => true;

    // ---- pictures ----

    /// <summary>Android's photo picker (no storage permission).</summary>
    public async Task<IReadOnlyList<BugImageDraft>> PickAsync(int max)
    {
        if (max <= 0) return [];
        Intent intent;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            intent = new Intent(MediaStore.ActionPickImages);
            if (max > 1) intent.PutExtra(MediaStore.ExtraPickImagesMax, Math.Min(max, MediaStore.PickImagesMaxLimit));
        }
        else
        {
            intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
            intent.PutExtra(Intent.ExtraAllowMultiple, max > 1);
        }
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data is not { } data) return [];
        var uris = new List<AndroidUri>();
        if (data.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
                if (clip.GetItemAt(i)?.Uri is { } u) uris.Add(u);
        }
        else if (data.Data is { } single)
        {
            uris.Add(single);
        }
        return await Task.Run(() => uris.Take(max).Select(FromUri).OfType<BugImageDraft>().ToList()).ConfigureAwait(true);
    }

    /// <summary>The camera app writes into Helm's cache through the FileProvider (no camera permission asked).</summary>
    public async Task<BugImageDraft?> TakePhotoAsync()
    {
        var context = AndroidApp.Context;
        var folder = Path.Combine(context.CacheDir!.AbsolutePath, CameraFolder);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"photo-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jpg");
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".updates", new Java.IO.File(file))!;
        var intent = new Intent(MediaStore.ActionImageCapture);
        intent.PutExtra(MediaStore.ExtraOutput, uri);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        intent.ClipData = ClipData.NewRawUri("photo", uri);
        try
        {
            var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
            if (!outcome.Ok || !File.Exists(file) || new FileInfo(file).Length == 0) return null;
            return await Task.Run(() => Prepare(() => File.OpenRead(file), file, "Photo.jpg")).ConfigureAwait(true);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>A picture copied to the clipboard (as a content URI), if there is one.</summary>
    public async Task<IReadOnlyList<BugImageDraft>> PasteAsync()
    {
        var context = AndroidApp.Context;
        if (context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard || clipboard.PrimaryClip is not { ItemCount: > 0 } clip) return [];
        if (clip.GetItemAt(0)?.Uri is not { } uri) return [];
        return await Task.Run(() => FromUri(uri) is { } image ? (IReadOnlyList<BugImageDraft>)[image] : []).ConfigureAwait(true);
    }

    private BugImageDraft? FromUri(AndroidUri uri)
    {
        var resolver = AndroidApp.Context.ContentResolver!;
        try
        {
            if (resolver.GetType(uri) is { } type && !type.StartsWith("image/", StringComparison.Ordinal)) return null;
            return Prepare(() => resolver.OpenInputStream(uri) ?? throw new IOException("The picture cannot be read."), null, uri.LastPathSegment);
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
        {
            logger.LogWarning(ex, "A picture for a bug report could not be read");
            return null;
        }
    }

    /// <summary>Upright, at most <see cref="MaxSide"/> on its longer side, as JPEG.</summary>
    private static BugImageDraft? Prepare(Func<Stream> open, string? file, string? name)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var s = open()) BitmapFactory.DecodeStream(s, null, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxSide) sample *= 2;
        Bitmap? bitmap;
        using (var s = open()) bitmap = BitmapFactory.DecodeStream(s, null, new BitmapFactory.Options { InSampleSize = sample });
        if (bitmap is null) return null;
        var rotation = 0;
        try
        {
            using var s = open();
            var orientation = new ExifInterface(s).GetAttributeInt(ExifInterface.TagOrientation, (int)Orientation.Normal);
            rotation = orientation switch { (int)Orientation.Rotate90 => 90, (int)Orientation.Rotate180 => 180, (int)Orientation.Rotate270 => 270, _ => 0 };
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
        {
            // No orientation: keep it as decoded.
        }
        var scale = Math.Min(1f, MaxSide / (float)Math.Max(bitmap.Width, bitmap.Height));
        if (rotation != 0 || scale < 1f)
        {
            var matrix = new Matrix();
            matrix.PostScale(scale, scale);
            if (rotation != 0) matrix.PostRotate(rotation);
            var turned = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
            if (!ReferenceEquals(turned, bitmap)) bitmap.Recycle();
            bitmap = turned;
        }
        using var memory = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, memory);
        bitmap.Recycle();
        var shown = Path.GetFileNameWithoutExtension(name ?? file ?? "Picture");
        return new BugImageDraft(memory.ToArray(), "image/jpeg", $"{shown}.jpg");
    }

    // ---- shake ----

    private void StartShake()
    {
        if (_shake is not null || !_settings.Current.ShakeToReport) return;
        if (AndroidApp.Context.GetSystemService(Context.SensorService) is not SensorManager manager
            || manager.GetDefaultSensor(SensorType.Accelerometer) is not { } sensor) return;
        _shake = new ShakeListener(manager, () => ui.Post(OnShaken));
        manager.RegisterListener(_shake, sensor, SensorDelay.Ui);
    }

    private void StopShake()
    {
        if (_shake is null) return;
        _shake.Stop();
        _shake = null;
    }

    private void OnShaken()
    {
        if (ActivityHost.Current is null || navigator.ActiveTab?.Key == ReportKey) return;
        Vibrate();
        Report();
    }

    private static void Vibrate()
    {
        try
        {
            var vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? (AndroidApp.Context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator
#pragma warning disable CA1422 // Before Android 12 the vibrator is a service of its own.
                : AndroidApp.Context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
            vibrator?.Vibrate(VibrationEffect.CreateOneShot(40, VibrationEffect.DefaultAmplitude));
        }
        catch (Java.Lang.Exception)
        {
            // No vibration: the page still opens.
        }
    }

    /// <summary>Three strong jolts within a second; then quiet for a while so one shake opens one report.</summary>
    private sealed class ShakeListener(SensorManager manager, Action shaken) : Java.Lang.Object, ISensorEventListener
    {
        private const float Threshold = 2.6f;
        private const long WindowMs = 1000;
        private const long CooldownMs = 3000;
        private readonly Queue<long> _jolts = new();
        private long _last;

        public void Stop() => manager.UnregisterListener(this);

        public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
        {
        }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values is not { Count: >= 3 } v) return;
            var g = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) / SensorManager.GravityEarth;
            if (g < Threshold) return;
            var now = SystemClock.ElapsedRealtime();
            if (now - _last < CooldownMs) return;
            while (_jolts.Count > 0 && now - _jolts.Peek() > WindowMs) _jolts.Dequeue();
            _jolts.Enqueue(now);
            if (_jolts.Count < 3) return;
            _jolts.Clear();
            _last = now;
            shaken();
        }
    }

    // ---- notifications ----

    private void QueueCheck()
    {
        if (Interlocked.Exchange(ref _checkQueued, 1) == 1) return;
        ui.Post(() =>
        {
            Volatile.Write(ref _checkQueued, 0);
            Check();
        });
    }

    /// <summary>A question from an agent, or a fix this Helm now has: one notification each.</summary>
    private void Check()
    {
        foreach (var bug in store.All())
        {
            if (bug.Value.PendingQuestion is { } question && _notifiedQuestions.Add(question))
                Notify($"{bug.Value.FixingBy ?? "Claude Code"} asks about #{bug.Value.Code}", store.GetEntry(question)?.Text ?? "", bug.Id, NotificationId(question));
        }
        foreach (var bug in store.FixedIn(Shell.Services.AppInfo.Version))
        {
            if (_notifiedFixes.Add(bug.Id))
                Notify($"Is bug #{bug.Value.Code} fixed?", $"Helm {Shell.Services.AppInfo.Version} has a fix for \"{bug.Value.Title}\". Tap to check it.", bug.Id, NotificationId(bug.Id + "/fix"));
        }
    }

    private void Notify(string title, string text, string open, int id)
    {
        try
        {
            var context = AndroidApp.Context;
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return;
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager || !manager.AreNotificationsEnabled()) return;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Bug reports", NotificationImportance.Default)
            {
                Description = "An AI agent's question about a bug, a fix to check, a crash to report.",
            });
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(Resource.Drawable.bug_report_tile)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetStyle(new Notification.BigTextStyle().BigText(text))
                .SetAutoCancel(true);
            if (context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!) is { } launch)
            {
                launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                launch.PutExtra(ExtraBug, open);
                builder.SetContentIntent(PendingIntent.GetActivity(context, id, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
            }
            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show a bug report notification");
        }
    }

    /// <summary>The same id for the same thing in any process (string hashes change per process).</summary>
    private static int NotificationId(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key) hash = (hash ^ c) * 16777619u;
        return 0x4C000000 | (int)(hash & 0xFFFFF);
    }
}
