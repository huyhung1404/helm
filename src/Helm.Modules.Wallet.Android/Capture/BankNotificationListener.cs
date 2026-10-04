using Android.Content;
using Android.Service.Notification;
using Helm.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using Notification = Android.App.Notification;
using NotificationFlags = Android.App.NotificationFlags;
using ServiceAttribute = Android.App.ServiceAttribute;

namespace Helm.Modules.Wallet;

/// <summary>
/// Reads the banks' notifications (their apps, and SMS from them) once the user grants Helm notification access, and
/// hands them to <see cref="WalletCapture"/>, which saves each transaction once. Android binds it in the app process,
/// with or without the activity, so services come from <see cref="HelmAndroidServices"/>. Every other app's
/// notification is dropped on the spot: Helm's services are not even started for it. The fixed name keeps the access
/// the user granted across updates.
/// </summary>
[Service(Name = "com.huyhung1404.helm.wallet.BankNotificationListener", Label = "Helm Wallet",
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE", Exported = false)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class BankNotificationListener : NotificationListenerService
{
    /// <summary>Asks Android to connect the listener again (it may let it go to save memory).</summary>
    public static void Rebind(Context context)
    {
        try
        {
            RequestRebind(new ComponentName(context, Java.Lang.Class.FromType(typeof(BankNotificationListener))));
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask Android to reconnect the Wallet listener");
        }
    }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        // Notifications that came while the listener was not connected (after an update or a reboot) are still in the
        // shade: read them too (each transaction is saved once).
        try
        {
            foreach (var n in GetActiveNotifications() ?? Array.Empty<StatusBarNotification>()) Read(n);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not read the notifications already shown");
        }
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        try
        {
            Read(sbn);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not read a notification");
        }
    }

    private void Read(StatusBarNotification? sbn)
    {
        if (sbn?.Notification is not { } notification || sbn.PackageName is not { } app || app == PackageName) return;
        // A group's summary repeats its children ("2 new messages").
        if ((notification.Flags & NotificationFlags.GroupSummary) != 0) return;
        var extras = notification.Extras;
        var title = extras?.GetCharSequence(Notification.ExtraTitle)?.ToString() ?? "";
        if (BankSources.Identify(app, title) is null) return;

        // The big text is the whole message; an inbox-style notification has one line per message.
        var texts = new List<string>();
        var main = extras?.GetCharSequence(Notification.ExtraBigText)?.ToString();
        if (string.IsNullOrWhiteSpace(main)) main = extras?.GetCharSequence(Notification.ExtraText)?.ToString();
        if (!string.IsNullOrWhiteSpace(main)) texts.Add(main);
        var lines = extras?.GetCharSequenceArray(Notification.ExtraTextLines);
        if (lines is not null)
        {
            foreach (var line in lines)
            {
                var s = line?.ToString();
                if (!string.IsNullOrWhiteSpace(s) && !texts.Any(t => t.Contains(s, StringComparison.Ordinal))) texts.Add(s);
            }
        }
        if (texts.Count == 0) return;
        var posted = DateTimeOffset.FromUnixTimeMilliseconds(sbn.PostTime);
        var context = ApplicationContext ?? this;
        // Off the main thread: the first notification after a cold start builds Helm's services.
        _ = Task.Run(() => Save(context, app, title, texts, posted));
    }

    private static void Save(Context context, string app, string title, List<string> texts, DateTimeOffset posted)
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var capture = services.GetRequiredService<WalletCapture>();
            var store = services.GetRequiredService<WalletStore>();
            var added = false;
            foreach (var text in texts)
            {
                var result = capture.Handle(app, title, text, posted, TimeZoneInfo.Local);
                if (result is not { Outcome: NotificationOutcome.Added, Capture: { } saved }) continue;
                added = true;
                // Money out that Helm could not file by itself: ask what it was.
                if (capture.Settings.Current.AskForCategory && saved.Transaction.Amount < 0 && !saved.Transaction.IsCategorized)
                    WalletNotifications.AskCategory(context, saved.Id, saved.Transaction, store.Suggest(saved.Transaction, WalletNotifications.MaxActions));
            }
            if (added) WalletWidgets.RefreshAll(context);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not save a transaction from a notification");
        }
    }

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Wallet.Listener").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}
