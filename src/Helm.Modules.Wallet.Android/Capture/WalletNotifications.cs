using Android.Content;
using Android.Content.PM;
using Helm.Ai.Assistant;
using Helm.Core;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Icon = Android.Graphics.Drawables.Icon;
using Notification = Android.App.Notification;
using NotificationChannel = Android.App.NotificationChannel;
using NotificationImportance = Android.App.NotificationImportance;
using NotificationManager = Android.App.NotificationManager;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

[assembly: Android.App.UsesPermission("android.permission.POST_NOTIFICATIONS")]

namespace Helm.Modules.Wallet;

/// <summary>
/// "−65,000 ₫ · Techcombank — what was it?": after a payment Helm could not file by itself, a quiet notification with
/// "Nói" (opens Ask Helm about it, listening) and the two likeliest categories as buttons (<see cref="WalletCategoryReceiver"/>
/// files it), AI's pick or proposed new category first once it answered; a tap opens Wallet. Runs without the activity.
/// </summary>
internal static class WalletNotifications
{
    /// <summary>Android shows at most three buttons.</summary>
    public const int MaxActions = 3;

    public const string ActionCategorize = "com.huyhung1404.helm.wallet.CATEGORIZE";
    public const string ExtraTransaction = "com.huyhung1404.helm.wallet.extra.TRANSACTION";
    public const string ExtraCategory = "com.huyhung1404.helm.wallet.extra.CATEGORY";

    /// <summary>With <see cref="ActionCategorize"/>: the name of a category AI proposed, created only by this tap.</summary>
    public const string ExtraNewCategory = "com.huyhung1404.helm.wallet.extra.NEW_CATEGORY";
    public const string ExtraNewKind = "com.huyhung1404.helm.wallet.extra.NEW_KIND";
    public const string ActionAddGap = "com.huyhung1404.helm.wallet.ADD_GAP";
    public const string ExtraBank = "com.huyhung1404.helm.wallet.extra.BANK";
    public const string ExtraAccount = "com.huyhung1404.helm.wallet.extra.ACCOUNT";
    public const string ExtraAmount = "com.huyhung1404.helm.wallet.extra.AMOUNT";
    public const string ExtraAfter = "com.huyhung1404.helm.wallet.extra.AFTER";
    public const string ExtraBefore = "com.huyhung1404.helm.wallet.extra.BEFORE";
    private const string ChannelId = "wallet_categorize";
    private const string FundChannelId = "wallet_funds";
    private const int PermissionRequestCode = 0x5A13;
    private const string PermissionAskedKey = "notification_permission_asked";

    /// <param name="proposal">AI's answer (<see cref="WalletCategoryAi"/>), shown first; null before it answered.</param>
    /// <param name="category">Looks up AI's pick when the history did not suggest it.</param>
    public static void AskCategory(Context context, string transactionId, WalletTransaction transaction, IReadOnlyList<CategoryInfo> suggestions,
        CategoryProposal? proposal = null, Func<string, CategoryInfo?>? category = null)
    {
        try
        {
            if (!CanNotify(context) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            // Quiet: the bank's own notification has already made a sound.
            var channel = new NotificationChannel(ChannelId, "What was it?", NotificationImportance.Default)
            {
                Description = "After a payment, a few categories to pick from.",
            };
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            manager.CreateNotificationChannel(channel);

            var id = NotificationId(transactionId);
            var title = transaction.Bank.Length > 0 ? $"{WalletFormat.Signed(transaction.Amount)} · {transaction.Bank}" : WalletFormat.Signed(transaction.Amount);
            // "Nói" first (the user says what it was and Ask Helm fills it in), then the two likeliest categories.
            var mic = CanAskHelm(context);
            var buttons = CategoryButtons.ForNotification(suggestions, proposal, category ?? (wanted => suggestions.FirstOrDefault(c => c.Id == wanted)));
            var guess = proposal is null ? null : buttons.FirstOrDefault()?.NewName ?? buttons.FirstOrDefault()?.Label;
            var question = guess is null ? "What was it?" : $"What was it? Maybe {guess}.";
            var text = transaction.Description.Length > 0 ? $"{transaction.Description}\n{question}" : question;
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(R.Drawable(context, "wallet_notification"))
                .SetContentTitle(title)
                .SetContentText(text)
                .SetStyle(new Notification.BigTextStyle().BigText(text))
                .SetAutoCancel(true)
                .SetOnlyAlertOnce(true)
                .SetWhen(transaction.OccurredAt.ToUnixTimeMilliseconds())
                .SetShowWhen(true);
            if (OpenWalletIntent(context, id) is { } open) builder.SetContentIntent(open);
            var icon = Icon.CreateWithResource(context, R.Drawable(context, "wallet_notification"));
            if (mic && AskHelmIntent(context, transactionId, transaction, id) is { } ask)
                builder.AddAction(new Notification.Action.Builder(icon, CategoryButtons.SayLabel, ask).Build());
            for (var i = 0; i < buttons.Count; i++)
            {
                var intent = new Intent(context, Java.Lang.Class.FromType(typeof(WalletCategoryReceiver)))
                    .SetAction(ActionCategorize)
                    .PutExtra(ExtraTransaction, transactionId);
                if (buttons[i].NewName is { } name) intent.PutExtra(ExtraNewCategory, name).PutExtra(ExtraNewKind, (int)buttons[i].Kind);
                else intent.PutExtra(ExtraCategory, buttons[i].CategoryId);
                var pending = PendingIntent.GetBroadcast(context, id * 4 + i, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.AddAction(new Notification.Action.Builder(icon, buttons[i].Label, pending).Build());
            }
            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask for the category of a transaction");
        }
    }

    /// <summary>Whether Ask Helm's card can open here: Quick Capture is on (its activity enabled) and it has a way to answer.</summary>
    private static bool CanAskHelm(Context context)
    {
        try
        {
            var state = context.PackageManager?.GetComponentEnabledSetting(new ComponentName(context.PackageName!, AskHelmContext.ActivityName));
            if (state is ComponentEnabledState.Disabled or ComponentEnabledState.DisabledUser) return false;
            return HelmAndroidServices.Current.GetService<HelmAssistant>()?.Unavailable() is null;
        }
        catch (Exception ex)
        {
            Log(ex, "Could not tell whether Ask Helm can open");
            return false;
        }
    }

    /// <summary>Opens Ask Helm's card about this transaction; it clears this notification once the answer saved it.</summary>
    private static PendingIntent? AskHelmIntent(Context context, string transactionId, WalletTransaction transaction, int notificationId)
    {
        var ask = WalletAskContext.For(transactionId, transaction);
        var intent = new Intent()
            .SetClassName(context.PackageName!, AskHelmContext.ActivityName)
            .AddFlags(ActivityFlags.NewTask)
            .PutExtra(AskHelmContext.ExtraTitle, ask.Title)
            .PutExtra(AskHelmContext.ExtraPrompt, ask.Prompt)
            .PutExtra(AskHelmContext.ExtraGroups, string.Join(",", ask.Groups))
            .PutExtra(AskHelmContext.ExtraNotification, notificationId);
        return PendingIntent.GetActivity(context, notificationId * 4 + 3, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    /// <summary>Whether the "what was it?" notification of a transaction is still shown (AI's answer updates only that).</summary>
    public static bool IsAsking(Context context, string transactionId)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return false;
        var id = NotificationId(transactionId);
        return manager.GetActiveNotifications()?.Any(n => n.Id == id) == true;
    }

    /// <summary>
    /// Money moved without a notification (the balance says so): offers the likeliest categories and "Later" (saved to
    /// categorize). Nothing is saved unless a button is tapped; swiping it away forgets it.
    /// </summary>
    public static void AskGap(Context context, BalanceGap gap, IReadOnlyList<CategoryInfo> suggestions)
    {
        try
        {
            if (!CanNotify(context) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            var channel = new NotificationChannel(ChannelId, "What was it?", NotificationImportance.Default)
            {
                Description = "After a payment, a few categories to pick from.",
            };
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            manager.CreateNotificationChannel(channel);

            var id = NotificationId(GapKey(gap));
            var title = $"{WalletFormat.Signed(gap.Amount)} · {gap.Bank}";
            var text = (gap.Amount < 0 ? "Went out" : "Came in") + " without a notification: the bank's balance says so. What was it?";
            var builder = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(R.Drawable(context, "wallet_notification"))
                .SetContentTitle(title)
                .SetContentText(text)
                .SetStyle(new Notification.BigTextStyle().BigText(text))
                .SetAutoCancel(true)
                .SetOnlyAlertOnce(true)
                .SetWhen(gap.Before.ToUnixTimeMilliseconds())
                .SetShowWhen(true);
            if (OpenWalletIntent(context, id) is { } open) builder.SetContentIntent(open);
            var icon = Icon.CreateWithResource(context, R.Drawable(context, "wallet_notification"));
            var buttons = suggestions.Take(MaxActions - 1).Select(c => (c.Name, (string?)c.Id)).Append(("Later", null)).ToList();
            for (var i = 0; i < buttons.Count; i++)
            {
                var intent = new Intent(context, Java.Lang.Class.FromType(typeof(WalletGapReceiver)))
                    .SetAction(ActionAddGap)
                    .PutExtra(ExtraBank, gap.Bank)
                    .PutExtra(ExtraAccount, gap.Account)
                    .PutExtra(ExtraAmount, gap.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .PutExtra(ExtraAfter, gap.After.ToUnixTimeMilliseconds())
                    .PutExtra(ExtraBefore, gap.Before.ToUnixTimeMilliseconds())
                    .PutExtra(ExtraCategory, buttons[i].Item2);
                var pending = PendingIntent.GetBroadcast(context, id * 4 + i, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.AddAction(new Notification.Action.Builder(icon, buttons[i].Name, pending).Build());
            }
            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask about money that moved without a notification");
        }
    }

    /// <summary>"Eating is running low · 182,000 ₫ left": a fund past its warning share, used up or over. A tap opens Wallet.</summary>
    public static void WarnFund(Context context, FundAlerts.Warning warning)
    {
        try
        {
            if (!CanNotify(context) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
            manager.CreateNotificationChannel(new NotificationChannel(FundChannelId, "Fund warnings", NotificationImportance.Default)
            {
                Description = "When a fund runs low, is used up or goes over.",
            });
            // One per fund: a worse warning replaces the earlier one.
            var id = NotificationId("fund|" + warning.FundId);
            var builder = new Notification.Builder(context, FundChannelId)
                .SetSmallIcon(R.Drawable(context, "wallet_notification"))
                .SetContentTitle(warning.Title)
                .SetContentText(warning.Text)
                .SetStyle(new Notification.BigTextStyle().BigText(warning.Text))
                .SetAutoCancel(true)
                .SetShowWhen(true);
            if (OpenWalletIntent(context, id) is { } open) builder.SetContentIntent(open);
            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            Log(ex, "Could not warn about a fund");
        }
    }

    /// <summary>The same notification id for the same gap, so answering it clears it.</summary>
    internal static string GapKey(BalanceGap gap) => $"gap|{gap.Bank}|{gap.Account}|{gap.Before.ToUnixTimeMilliseconds()}";

    public static void CancelGap(Context context, BalanceGap gap)
    {
        if (context.GetSystemService(Context.NotificationService) is NotificationManager manager) manager.Cancel(NotificationId(GapKey(gap)));
    }

    public static void Cancel(Context context, string transactionId)
    {
        if (context.GetSystemService(Context.NotificationService) is NotificationManager manager) manager.Cancel(NotificationId(transactionId));
    }

    /// <summary>The same id for the same transaction in any process (string hashes change per process).</summary>
    private static int NotificationId(string transactionId)
    {
        var hash = 2166136261u;
        foreach (var c in transactionId) hash = (hash ^ c) * 16777619u;
        return 0x5A000000 | (int)(hash & 0xFFFFF);
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
        var prefs = context.GetSharedPreferences("helm_wallet", FileCreationMode.Private);
        if (prefs is null || prefs.GetBoolean(PermissionAskedKey, false) || ActivityHost.Current is not { } activity) return;
        prefs.Edit()?.PutBoolean(PermissionAskedKey, true)?.Apply();
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequestCode);
    }

    /// <summary>Opens Helm on the Wallet page (MainActivity reads the module extra).</summary>
    internal static PendingIntent? OpenWalletIntent(Context context, int requestCode)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, WalletIds.ModuleId);
        return PendingIntent.GetActivity(context, requestCode, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    internal static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Wallet").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}

/// <summary>A category button of the "what was it?" notification: files the transaction and clears the notification.</summary>
[BroadcastReceiver(Name = "com.huyhung1404.helm.wallet.CategoryReceiver", Exported = false)]
public sealed class WalletCategoryReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != WalletNotifications.ActionCategorize) return;
        var transactionId = intent.GetStringExtra(WalletNotifications.ExtraTransaction);
        var categoryId = intent.GetStringExtra(WalletNotifications.ExtraCategory);
        var newName = intent.GetStringExtra(WalletNotifications.ExtraNewCategory);
        if (string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(categoryId) && string.IsNullOrEmpty(newName)) return;
        try
        {
            var store = HelmAndroidServices.Current.GetRequiredService<WalletStore>();
            // AI's new category exists from this tap on (one of that name made meanwhile is the same one).
            if (string.IsNullOrEmpty(categoryId))
            {
                var kind = (CategoryKind)intent.GetIntExtra(WalletNotifications.ExtraNewKind, (int)CategoryKind.Expense);
                categoryId = store.Categories(includeHidden: true).FirstOrDefault(c => c.Kind == kind
                    && string.Equals(c.Name, newName!.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Id ?? store.AddCategory(newName!, kind);
            }
            store.SetCategory(transactionId, categoryId);
            WalletNotifications.Cancel(context, transactionId);
            WalletWidgets.RefreshAll(context);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not file a transaction from its notification");
        }
    }
}

/// <summary>A button of the "went out without a notification" reminder: saves the missing amount and clears it.</summary>
[BroadcastReceiver(Name = "com.huyhung1404.helm.wallet.GapReceiver", Exported = false)]
public sealed class WalletGapReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != WalletNotifications.ActionAddGap) return;
        var bank = intent.GetStringExtra(WalletNotifications.ExtraBank);
        var amountText = intent.GetStringExtra(WalletNotifications.ExtraAmount);
        if (string.IsNullOrEmpty(bank)
            || !decimal.TryParse(amountText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount == 0) return;
        var gap = new BalanceGap(bank, intent.GetStringExtra(WalletNotifications.ExtraAccount) ?? "", amount,
            DateTimeOffset.FromUnixTimeMilliseconds(intent.GetLongExtra(WalletNotifications.ExtraAfter, 0)),
            DateTimeOffset.FromUnixTimeMilliseconds(intent.GetLongExtra(WalletNotifications.ExtraBefore, 0)));
        try
        {
            var store = HelmAndroidServices.Current.GetRequiredService<WalletStore>();
            // A second tap (or another device that saved it meanwhile) finds it noted and saves nothing.
            if (StillMissing(store, gap)) store.AddGap(gap, intent.GetStringExtra(WalletNotifications.ExtraCategory));
            WalletNotifications.CancelGap(context, gap);
            WalletWidgets.RefreshAll(context);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not save money that moved without a notification");
        }
    }

    private static bool StillMissing(WalletStore store, BalanceGap gap) =>
        !store.Transactions().Any(t => t.Value.Source == TransactionSource.Manual && t.Value.Amount == gap.Amount
            && t.Value.OccurredAt >= gap.After && t.Value.OccurredAt <= gap.Before);
}

/// <summary>Resource ids looked up by name (the layouts and drawables live in this project's Resources).</summary>
internal static class R
{
    private static readonly Dictionary<string, int> s_cache = new(StringComparer.Ordinal);

    public static int Drawable(Context context, string name) => Get(context, name, "drawable");

    public static int Layout(Context context, string name) => Get(context, name, "layout");

    public static int Id(Context context, string name) => Get(context, name, "id");

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
