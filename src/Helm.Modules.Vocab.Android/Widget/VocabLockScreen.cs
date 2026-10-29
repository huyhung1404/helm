using Android.Content;
using Android.Content.PM;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using NotificationVisibility = Android.App.NotificationVisibility;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

namespace Helm.Modules.Vocab;

/// <summary>
/// A word on the lock screen, as a silent notification (Android has no lock-screen widgets): a new word each time the
/// screen turns on, "Show meaning" right there, then Forgot or Remembered without unlocking. A word whose meaning is
/// shown keeps its place until it is answered. Runs in the app process, possibly without the activity.
/// </summary>
internal static class VocabLockScreen
{
    public const string ActionReveal = "com.huyhung1404.helm.vocab.LOCK_REVEAL";
    public const string ActionRemembered = "com.huyhung1404.helm.vocab.LOCK_REMEMBERED";
    public const string ActionForgot = "com.huyhung1404.helm.vocab.LOCK_FORGOT";
    public const string ActionSkip = "com.huyhung1404.helm.vocab.LOCK_SKIP";

    private const string ChannelId = "vocab_word";
    private const int NotificationId = 4720;
    private const string PreferencesName = "helm_vocab_lock_screen";
    private const string WordKey = "word";
    private const string RevealedKey = "revealed";
    private const string PermissionAskedKey = "permission_asked";
    private const int PermissionRequestCode = 4721;

    /// <summary>The screen turned on: a new word, unless one waits for its answer.</summary>
    public static void NextWordIfIdle(Context context)
    {
        if (!IsRevealed(context)) Pick(context);
        Refresh(context);
    }

    public static void Reveal(Context context)
    {
        Preferences(context).Edit()?.PutBoolean(RevealedKey, true)?.Apply();
        Refresh(context);
    }

    /// <summary>Records the answer (synced like one given in the app), then the next word.</summary>
    public static void Answer(Context context, bool remembered)
    {
        if (WordId(context) is { } id && IsRevealed(context))
        {
            try
            {
                HelmAndroidServices.Current.GetRequiredService<VocabStore>().Record(id, remembered);
            }
            catch (Exception ex)
            {
                Log(ex, "Could not record a Vocabulary answer from the lock screen");
            }
        }
        Pick(context);
        Refresh(context);
    }

    public static void Skip(Context context)
    {
        Pick(context);
        Refresh(context);
    }

    /// <summary>Shows the current word again (after a change of data or settings), or takes it away when it is off.</summary>
    public static void Refresh(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<VocabStore>();
            if (!IsOn(services) || !CanNotify(context))
            {
                manager.Cancel(NotificationId);
                return;
            }
            var word = WordId(context) is { } id ? store.GetWord(id) : null;
            if (word is not { IsAnalyzed: true })
            {
                Pick(context);
                word = WordId(context) is { } next ? store.GetWord(next) : null;
            }
            if (word is null)
            {
                manager.Cancel(NotificationId);
                return;
            }
            manager.CreateNotificationChannel(Channel());
            manager.Notify(NotificationId, Build(context, store, WordId(context)!, word, IsRevealed(context)));
        }
        catch (Exception ex)
        {
            Log(ex, "Could not show the word on the lock screen");
        }
    }

    public static void Cancel(Context context) =>
        (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Cancel(NotificationId);

    public static bool CanNotify(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return false;
        return context.GetSystemService(Context.NotificationService) is NotificationManager { } m && m.AreNotificationsEnabled();
    }

    /// <summary>Android 13+: asks once for notifications, when the user turns the lock-screen word on or opens Vocabulary.</summary>
    public static void AskPermissionOnce(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CanNotify(context)) return;
        var prefs = Preferences(context);
        if (prefs.GetBoolean(PermissionAskedKey, false) || ActivityHost.Current is not { } activity) return;
        prefs.Edit()?.PutBoolean(PermissionAskedKey, true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }

    // ---- the notification ----------------------------------------------------------------------------------------

    /// <summary>Default importance with no sound or vibration: silent, yet shown on the lock screen (silent-importance ones may not be).</summary>
    private static NotificationChannel Channel()
    {
        var channel = new NotificationChannel(ChannelId, "Word on the lock screen", NotificationImportance.Default)
        {
            Description = "One of your words to guess each time the screen turns on. Silent.",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        channel.SetShowBadge(false);
        return channel;
    }

    private static Notification Build(Context context, VocabStore store, string id, VocabWord word, bool revealed)
    {
        var builder = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(R.Get(context, "vocab_notification", "drawable"))
            .SetVisibility(NotificationVisibility.Public)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetCategory(Notification.CategoryReminder);
        if (OpenWordIntent(context, id) is { } open) builder.SetContentIntent(open);
        if (!revealed)
        {
            builder.SetContentTitle(word.Text)
                .SetContentText("Do you remember it? Say the meaning in your head.")
                .AddAction(Action(context, ActionReveal, "Show meaning", 1))
                .AddAction(Action(context, ActionSkip, "Another word", 2));
            return builder.Build()!;
        }
        var reading = string.Join("  ", new[] { word.Phonetic, word.HanViet, word.Traditional }.Where(s => s.Length > 0));
        var lines = VocabTree.Lines(store, word);
        var details = string.Join(Environment.NewLine, new[] { word.Meaning }.Concat(lines).Append(word.Explanation).Where(s => s.Length > 0));
        builder.SetContentTitle(reading.Length > 0 ? $"{word.Text}   {reading}" : word.Text)
            .SetContentText(word.Meaning)
            .SetStyle(new Notification.BigTextStyle().BigText(details))
            .AddAction(Action(context, ActionForgot, "Forgot", 3))
            .AddAction(Action(context, ActionRemembered, "Remembered", 4));
        return builder.Build()!;
    }

    private static Notification.Action Action(Context context, string action, string title, int slot)
    {
        var intent = new Intent(context, typeof(VocabLockScreenReceiver)).SetAction(action)!;
        var pending = PendingIntent.GetBroadcast(context, NotificationId + slot, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
#pragma warning disable CA1422 // The icon-less action builder: fine on every version Helm runs on.
        return new Notification.Action.Builder(0, title, pending).Build()!;
#pragma warning restore CA1422
    }

    /// <summary>A tap on the notification (after unlocking): the word with its whole tree.</summary>
    private static PendingIntent? OpenWordIntent(Context context, string id)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, VocabIds.ModuleId);
        launch.PutExtra(ShellIntents.ExtraItem, id);
        return PendingIntent.GetActivity(context, NotificationId, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    // ---- state ---------------------------------------------------------------------------------------------------

    private static void Pick(Context context)
    {
        try
        {
            var next = HelmAndroidServices.Current.GetRequiredService<VocabQuiz>().Next(except: WordId(context));
            Preferences(context).Edit()?.PutString(WordKey, next?.Id)?.PutBoolean(RevealedKey, false)?.Apply();
        }
        catch (Exception ex)
        {
            Log(ex, "Could not pick a word for the lock screen");
        }
    }

    private static bool IsOn(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsStoreFactory>();
        var general = settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current;
        var enabled = !general.EnabledModules.TryGetValue(VocabIds.ModuleId, out var on) || on;
        return enabled && settings.Get<VocabSettings>(VocabIds.ModuleId).Current.LockScreenWord;
    }

    private static string? WordId(Context context) => Preferences(context).GetString(WordKey, null);

    private static bool IsRevealed(Context context) => Preferences(context).GetBoolean(RevealedKey, false);

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Vocab.LockScreen").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}

/// <summary>The lock-screen notification's buttons (they work without unlocking: no activity is started).</summary>
[BroadcastReceiver(Exported = false)]
public sealed class VocabLockScreenReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        switch (intent?.Action)
        {
            case VocabLockScreen.ActionReveal:
                VocabLockScreen.Reveal(context);
                break;
            case VocabLockScreen.ActionRemembered:
                VocabLockScreen.Answer(context, remembered: true);
                break;
            case VocabLockScreen.ActionForgot:
                VocabLockScreen.Answer(context, remembered: false);
                break;
            case VocabLockScreen.ActionSkip:
                VocabLockScreen.Skip(context);
                break;
        }
    }
}
