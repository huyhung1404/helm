using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlarmManager = Android.App.AlarmManager;
using AlarmType = Android.App.AlarmType;
using AndroidApp = Android.App.Application;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

namespace Helm.Modules.Vocab;

/// <summary>What "Show a word now" did.</summary>
public enum VocabShowResult
{
    Shown,
    NoPermission,
    NoWord,
}

/// <summary>
/// The phone's pop-up card and floating bubble (both over the other apps, never with a notification). The card comes
/// back after a random gap (<see cref="VocabPopupSchedule"/>) from an alarm that never wakes the phone; the bubble stays
/// while it is on. Both follow <see cref="VocabOverlayRules"/>: hidden sideways, over blocked apps, over Helm, and while
/// nobody uses the phone. While Helm's process runs it follows the screen, unlocks and turns at once; the bubble checks
/// the app in front every 2 seconds, only while the screen is on and some app is blocked. Main thread only (the alarms and
/// the receivers run there).
/// </summary>
public sealed class VocabOverlay
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);
    private const long PollMs = 2000;

    // The notification and channel of the lock-screen word (gone): cleared from phones that had it.
    private const int OldLockScreenNotification = 4720;
    private const string OldLockScreenChannel = "vocab_word";

    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly VocabVoice _voice;
    private readonly VocabSettingsViewModel _settings;
    private readonly ISettingsStoreFactory _settingsFactory;
    private readonly ILogger<VocabOverlay> _logger;
    private readonly Context _context = AndroidApp.Context;
    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly Action _poll;
    private IWindowManager? _windows;
    private VocabBubble? _bubble;
    private VocabCardOverlay? _card;
    private Action? _lingerTimeout;
    private ScreenReceiver? _screen;
    private TurnWatcher? _turns;
    private bool _started, _polling, _dismissedUntilUnlock;

    public VocabOverlay(VocabStore store, VocabQuiz quiz, VocabVoice voice, VocabSettingsViewModel settings, ISettingsStoreFactory settingsFactory,
        ILogger<VocabOverlay> logger)
    {
        _store = store;
        _quiz = quiz;
        _voice = voice;
        _settings = settings;
        _settingsFactory = settingsFactory;
        _logger = logger;
        _poll = Poll;
        _settings.Changed += (_, name) => _main.Post(() => OnSettingChanged(name));
    }

    /// <summary>Starts following the screen and the settings, and schedules the card. Harmless to call often.</summary>
    public void Start()
    {
        if (!Looper.MainLooper!.IsCurrentThread)
        {
            _main.Post(Start);
            return;
        }
        if (_started) return;
        _started = true;
        try
        {
            ClearOldLockScreen();
            _screen = new ScreenReceiver(this);
            var filter = new IntentFilter(Intent.ActionScreenOn);
            filter.AddAction(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionUserPresent);
            _context.RegisterReceiver(_screen, filter);
            _turns = new TurnWatcher(this);
            _context.RegisterComponentCallbacks(_turns);
            ActivityHost.Resumed += OnHelmShown;
            ActivityHost.Paused += OnHelmShown;
            VocabOverlayAlarms.KeepAlive(_context);
            ScheduleCard(keepPending: true);
            UpdateBubble();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the Vocabulary pop-up and bubble");
        }
    }

    /// <summary>Removes the card and the bubble and stops every alarm (Vocabulary turned off).</summary>
    public void Stop()
    {
        if (!Looper.MainLooper!.IsCurrentThread)
        {
            _main.Post(Stop);
            return;
        }
        if (!_started) return;
        _started = false;
        try
        {
            if (_screen is not null) _context.UnregisterReceiver(_screen);
            if (_turns is not null) _context.UnregisterComponentCallbacks(_turns);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop following the screen");
        }
        _screen = null;
        _turns = null;
        ActivityHost.Resumed -= OnHelmShown;
        ActivityHost.Paused -= OnHelmShown;
        _main.RemoveCallbacks(_poll);
        _polling = false;
        VocabOverlayAlarms.CancelAll(_context);
        CloseCard();
        _bubble?.Hide();
    }

    /// <summary>A Vocabulary page is on screen, or the user came back from the phone's settings: look again.</summary>
    public void Refresh()
    {
        if (!_started) return;
        ScheduleCard(keepPending: true);
        UpdateBubble();
    }

    /// <summary>"Show a word now" (settings page): the card at once, wherever the phone is.</summary>
    public VocabShowResult ShowNow()
    {
        if (!VocabOverlayPlatform.CanDraw(_context)) return VocabShowResult.NoPermission;
        return ShowCard(nearBubble: false) ? VocabShowResult.Shown : VocabShowResult.NoWord;
    }

    // ---- The card ------------------------------------------------------------------------------------------------

    /// <summary>The card's alarm went off: show it if the phone allows, or try again a little later.</summary>
    public void OnCardAlarm()
    {
        if (!_started) return;
        var s = _settings.Current;
        var verdict = VocabOverlayRules.Card(State(withApp: true, hasWord: _quiz.Next() is not null), s.PopupEnabled);
        _logger.LogDebug("Vocabulary pop-up: {Verdict}", verdict);
        switch (verdict)
        {
            case VocabOverlayVerdict.Show:
                ShowCard(nearBubble: false);
                ScheduleCard(keepPending: false);
                break;
            case VocabOverlayVerdict.Off:
                VocabOverlayAlarms.CancelCard(_context);
                break;
            default:
                VocabOverlayAlarms.ScheduleCard(_context, VocabPopupSchedule.RetryAfter(s.PopupMaxSeconds));
                break;
        }
    }

    /// <summary>The next card after a random gap; with <paramref name="keepPending"/> one already set stays as it is.</summary>
    private void ScheduleCard(bool keepPending)
    {
        var s = _settings.Current;
        if (!s.PopupEnabled)
        {
            VocabOverlayAlarms.CancelCard(_context);
            return;
        }
        if (keepPending && VocabOverlayAlarms.CardPending(_context, TimeSpan.FromSeconds(s.PopupMaxSeconds))) return;
        VocabOverlayAlarms.ScheduleCard(_context, VocabPopupSchedule.NextDelay(s.PopupMinSeconds, s.PopupMaxSeconds));
    }

    private bool ShowCard(bool nearBubble)
    {
        if (_card is not null) return true;
        // A Chinese word asks for its pinyin first when the Learn tab does (the same setting).
        var card = new VocabCardViewModel(_store, _quiz, voice: _voice,
            options: () => new VocabCardOptions(VocabCardMode.WordToMeaning, false, _settings.Current.TypePinyin))
        {
            ShowYouGlish = _settings.Current.ShowYouGlish,
        };
        if (!card.ShowNext()) return false;
        try
        {
            var windows = Windows();
            var metrics = _context.Resources!.DisplayMetrics!;
            var width = Math.Min(metrics.WidthPixels - Dp(24), Dp(420));
            var p = Params(width, ViewGroup.LayoutParams.WrapContent, touchable: true);
            p.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
            p.SoftInputMode = SoftInput.StateHidden | SoftInput.AdjustNothing;
            p.Y = nearBubble && _bubble is { IsShown: true } bubble
                ? Math.Min(bubble.Bounds.Y + bubble.Bounds.Size + Dp(8), (int)(metrics.HeightPixels * 0.45))
                : Dp(48);
            var restY = p.Y;
            VocabCardOverlay? view = null;
            view = new VocabCardOverlay(_context, card, CloseCard, OpenWord,
                keyboard: typing =>
                {
                    // Typing: the card takes the keyboard and moves to the top, above it; done, it hands both back.
                    if (typing) p.Flags &= ~WindowManagerFlags.NotFocusable;
                    else p.Flags |= WindowManagerFlags.NotFocusable;
                    p.Y = typing ? Math.Min(p.Y, Dp(48)) : restY;
                    view!.MaxHeight = typing ? (int)(metrics.HeightPixels * 0.42) : CompactHeight(metrics);
                    Update(view, p);
                },
                full: full =>
                {
                    p.Y = full ? Dp(36) : restY;
                    view!.MaxHeight = full ? metrics.HeightPixels - Dp(72) : CompactHeight(metrics);
                    Update(view, p);
                })
            {
                MaxHeight = CompactHeight(metrics),
            };
            windows.AddView(view.View, p);
            _card = view;
            // Unread, it goes away by itself; being typed in or read, it waits for an answer or ✕.
            _lingerTimeout = () =>
            {
                if (_card is { IsBusy: false }) CloseCard();
            };
            _main.PostDelayed(_lingerTimeout, (long)Linger.TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Vocabulary pop-up");
            return false;
        }
    }

    /// <summary>The card's height before it scrolls: about two thirds of the screen.</summary>
    private static int CompactHeight(Android.Util.DisplayMetrics metrics) => (int)(metrics.HeightPixels * 0.62);

    private void Update(VocabCardOverlay view, WindowManagerLayoutParams p)
    {
        try
        {
            Windows().UpdateViewLayout(view.View, p);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Closed meanwhile.
        }
    }

    private void CloseCard()
    {
        if (_lingerTimeout is not null) _main.RemoveCallbacks(_lingerTimeout);
        _lingerTimeout = null;
        if (_card is not { } card) return;
        _card = null;
        card.Detach();
        try
        {
            Windows().RemoveView(card.View);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>"Open in Helm": Vocabulary with that word and its whole tree.</summary>
    private void OpenWord(string wordId)
    {
        var launch = _context.PackageManager?.GetLaunchIntentForPackage(_context.PackageName!);
        if (launch is null) return;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, VocabIds.ModuleId);
        launch.PutExtra(ShellIntents.ExtraItem, wordId);
        _context.StartActivity(launch);
    }

    // ---- The bubble ----------------------------------------------------------------------------------------------

    /// <summary>Shows or hides the bubble as the rules say now, and keeps checking the app in front while it matters.</summary>
    public void UpdateBubble()
    {
        if (!_started) return;
        var s = _settings.Current;
        var verdict = VocabOverlayRules.Bubble(State(withApp: s.BlockedApps.Count > 0, hasWord: true), s.BubbleEnabled, _dismissedUntilUnlock);
        try
        {
            if (verdict == VocabOverlayVerdict.Show)
            {
                _bubble ??= new VocabBubble(_context, Windows(), BubbleTapped, () => _dismissedUntilUnlock = true,
                    (x, y) => _settings.SaveBubblePosition(x, y));
                if (!_bubble.IsShown) _bubble.Show(s.BubbleX, s.BubbleY);
            }
            else
            {
                _bubble?.Hide();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the Vocabulary bubble");
        }
        // Only an app the user blocked needs watching; nothing to watch with the screen off.
        var watch = s.BubbleEnabled && s.BlockedApps.Count > 0 && VocabOverlayPlatform.InUse(_context) && VocabOverlayPlatform.HasUsageAccess(_context);
        if (watch && !_polling)
        {
            _polling = true;
            _main.PostDelayed(_poll, PollMs);
        }
        else if (!watch && _polling)
        {
            _polling = false;
            _main.RemoveCallbacks(_poll);
        }
    }

    /// <summary>The bubble was tapped: a word next to it, or a word on why there is none.</summary>
    private void BubbleTapped()
    {
        if (ShowCard(nearBubble: true)) return;
        Android.Widget.Toast.MakeText(_context, "No word is ready yet: words show once an AI agent has broken them down.", Android.Widget.ToastLength.Long)?.Show();
    }

    private void Poll()
    {
        if (!_polling) return;
        _polling = false;
        UpdateBubble();
    }

    // ---- Following the phone -------------------------------------------------------------------------------------

    private VocabOverlayState State(bool withApp, bool hasWord)
    {
        var helmInFront = ActivityHost.Current is not null;
        return new VocabOverlayState
        {
            ModuleOn = ModuleOn(),
            CanDraw = VocabOverlayPlatform.CanDraw(_context),
            InUse = VocabOverlayPlatform.InUse(_context),
            Landscape = VocabOverlayPlatform.Landscape(_context),
            OwnApp = _context.PackageName,
            // Helm knows when it is in front itself; other apps need usage access, asked only when some app is blocked.
            ForegroundApp = helmInFront ? _context.PackageName : withApp ? VocabOverlayPlatform.ForegroundApp(_context) : null,
            BlockedApps = _settings.Current.BlockedApps,
            HasWord = hasWord,
        };
    }

    private bool ModuleOn()
    {
        var general = _settingsFactory.Get<GeneralSettings>(GeneralSettings.StoreId).Current;
        return !general.EnabledModules.TryGetValue(VocabIds.ModuleId, out var on) || on;
    }

    private void OnSettingChanged(string name)
    {
        if (!_started) return;
        switch (name)
        {
            case nameof(VocabSettingsViewModel.PopupEnabled):
            case nameof(VocabSettingsViewModel.PopupFrequencyIndex):
                ScheduleCard(keepPending: false);
                break;
            case nameof(VocabSettingsViewModel.BubbleEnabled):
                _dismissedUntilUnlock = false;
                UpdateBubble();
                break;
            case nameof(VocabSettingsViewModel.BlockedApps):
                UpdateBubble();
                break;
        }
    }

    private void OnHelmShown(object? sender, EventArgs e) => _main.Post(UpdateBubble);

    private void OnScreen(string? action)
    {
        if (action == Intent.ActionUserPresent) _dismissedUntilUnlock = false;
        if (action == Intent.ActionScreenOff) CloseCard();
        UpdateBubble();
    }

    private void OnTurned()
    {
        if (VocabOverlayPlatform.Landscape(_context)) CloseCard();
        UpdateBubble();
    }

    private void ClearOldLockScreen()
    {
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager notifications) return;
        notifications.Cancel(OldLockScreenNotification);
        notifications.DeleteNotificationChannel(OldLockScreenChannel);
    }

    private IWindowManager Windows() => _windows ??= _context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;

    /// <summary>An overlay window over the other apps that never takes the keyboard; touches outside go to the app below.</summary>
    internal static WindowManagerLayoutParams Params(int width, int height, bool touchable) => new(width, height,
        WindowManagerTypes.ApplicationOverlay,
        WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen
            | (touchable ? 0 : WindowManagerFlags.NotTouchable),
        Format.Translucent);

    private int Dp(double dp) => VocabOverlayPlatform.Dp(_context, dp);

    /// <summary>Screen on and off, and unlocks (Android delivers them only to a receiver registered while running).</summary>
    private sealed class ScreenReceiver(VocabOverlay overlay) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => overlay.OnScreen(intent?.Action);
    }

    /// <summary>The phone turned (or the theme changed).</summary>
    private sealed class TurnWatcher(VocabOverlay overlay) : Java.Lang.Object, IComponentCallbacks
    {
        public void OnConfigurationChanged(Configuration newConfig) => overlay.OnTurned();

        public void OnLowMemory()
        {
        }
    }
}

/// <summary>
/// The two alarms of the pop-up and the bubble. Both are RTC, never RTC_WAKEUP: with the screen off they wait for the
/// phone to wake by itself, so they cost no battery while it sleeps. The card's alarm goes off once, at the next random
/// time; the hourly one puts the bubble back after Android stopped Helm. When the card is due is kept in preferences,
/// so a restarted Helm does not push it back.
/// </summary>
internal static class VocabOverlayAlarms
{
    public const string ActionCard = "com.huyhung1404.helm.vocab.POPUP";
    public const string ActionKeep = "com.huyhung1404.helm.vocab.KEEP";
    private const int CardRequest = 0x7D11;
    private const int KeepRequest = 0x7D12;
    private const string Prefs = "helm_vocab_overlay";
    private const string NextCardKey = "next_card";

    public static void ScheduleCard(Context context, TimeSpan after)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var at = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)after.TotalMilliseconds;
        alarms.Set(AlarmType.Rtc, at, Intent(context, ActionCard, CardRequest));
        context.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutLong(NextCardKey, at)?.Apply();
    }

    /// <summary>A card is already due within the longest gap (not one long overdue from before a restart).</summary>
    public static bool CardPending(Context context, TimeSpan longestGap)
    {
        var at = context.GetSharedPreferences(Prefs, FileCreationMode.Private)?.GetLong(NextCardKey, 0) ?? 0;
        var now = Java.Lang.JavaSystem.CurrentTimeMillis();
        return at > now && at - now <= (long)(longestGap + VocabPopupSchedule.Retry).TotalMilliseconds;
    }

    public static void CancelCard(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms) alarms.Cancel(Intent(context, ActionCard, CardRequest));
        context.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.Remove(NextCardKey)?.Apply();
    }

    public static void KeepAlive(Context context)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
        var first = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)TimeSpan.FromHours(1).TotalMilliseconds;
        alarms.SetInexactRepeating(AlarmType.Rtc, first, AlarmManager.IntervalHour, Intent(context, ActionKeep, KeepRequest));
    }

    public static void CancelAll(Context context)
    {
        CancelCard(context);
        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms) alarms.Cancel(Intent(context, ActionKeep, KeepRequest));
    }

    private static PendingIntent Intent(Context context, string action, int request)
    {
        var intent = new Intent(context, typeof(VocabOverlayReceiver)).SetAction(action)!;
        return PendingIntent.GetBroadcast(context, request, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
}

/// <summary>The card's alarm, the hourly check, and the restart after a reboot (alarms do not survive one).</summary>
[BroadcastReceiver(Exported = true)]
[Android.App.IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class VocabOverlayReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            if (general.EnabledModules.TryGetValue(VocabIds.ModuleId, out var on) && !on) return;
            var overlay = services.GetRequiredService<VocabOverlay>();
            overlay.Start();
            switch (intent?.Action)
            {
                case VocabOverlayAlarms.ActionCard:
                    overlay.OnCardAlarm();
                    break;
                case VocabOverlayAlarms.ActionKeep:
                    overlay.Refresh();
                    break;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Vocabulary pop-up alarm failed: {ex}");
        }
    }
}
