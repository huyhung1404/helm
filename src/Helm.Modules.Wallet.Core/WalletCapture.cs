using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Wallet;

public enum NotificationOutcome
{
    /// <summary>Not from a bank Helm listens to (or listening is off), or a one-time code, which is never kept.</summary>
    Ignored,

    /// <summary>From a bank, but not a transaction Helm could read; kept in the settings for the user to check.</summary>
    Unread,

    Added,

    Duplicate,
}

public sealed record NotificationResult(NotificationOutcome Outcome, BankSource? Bank = null, CaptureOutcome? Capture = null);

/// <summary>
/// What the phone's notification listener hands every notification to: picks the bank's ones, reads them and saves the
/// transaction (once). Platform-free, so the whole path is tested here. Never throws. Notification text is private:
/// it is never written to the log.
/// </summary>
public sealed class WalletCapture
{
    public const int MaxUnread = 20;

    private readonly WalletStore _store;
    private readonly ISettingsStoreFactory _settingsFactory;
    private readonly ISettingsStore<WalletSettings> _settings;
    private readonly ILogger _logger;

    public WalletCapture(WalletStore store, ISettingsStoreFactory settings, ILogger<WalletCapture>? logger = null)
    {
        _store = store;
        _settingsFactory = settings;
        _settings = settings.Get<WalletSettings>(WalletIds.ModuleId);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        // Older versions kept a one-time code that talked about money in the unread list: forget it.
        if (_settings.Current.Unread.Any(u => BankNotificationParser.HasOneTimeCode(u.Title, u.Text)))
            _settings.Update(s => s.Unread.RemoveAll(u => BankNotificationParser.HasOneTimeCode(u.Title, u.Text)));
    }

    public ISettingsStore<WalletSettings> Settings => _settings;

    /// <summary>Raised after a transaction was added from a notification (on the listener's thread).</summary>
    public event EventHandler<CaptureOutcome>? Captured;

    /// <summary>Whether the Wallet tool is on (missing from the list means on), read without the module registry.</summary>
    public bool IsToolEnabled
    {
        get
        {
            var general = _settingsFactory.Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            return !general.EnabledModules.TryGetValue(WalletIds.ModuleId, out var on) || on;
        }
    }

    /// <param name="app">The package name of the app that posted the notification.</param>
    /// <param name="title">Its title (the sender for an SMS).</param>
    /// <param name="text">Its text; the big text when there is one.</param>
    /// <param name="postedAt">When it was posted.</param>
    /// <param name="smsApp">The phone's default SMS app, when the platform knows it (see <see cref="BankSources.Identify"/>).</param>
    public NotificationResult Handle(string app, string? title, string? text, DateTimeOffset postedAt, TimeZoneInfo zone, string? smsApp = null)
    {
        try
        {
            var settings = _settings.Current;
            if (!settings.ListenEnabled || !IsToolEnabled) return new(NotificationOutcome.Ignored);
            if (BankSources.Identify(app, title ?? "", smsApp) is not { } bank || !settings.IsBankEnabled(bank.Id)) return new(NotificationOutcome.Ignored);
            if (BankNotificationParser.HasOneTimeCode(title, text)) return new(NotificationOutcome.Ignored, bank);

            var parsed = BankNotificationParser.Parse(bank.Name, title, text, postedAt, zone);
            if (parsed is null)
            {
                if (!LooksLikeMoney(title, text)) return new(NotificationOutcome.Ignored, bank);
                KeepUnread(bank, title, text, postedAt);
                _logger.LogInformation("Wallet: a {Bank} notification could not be read; kept for the user to check", bank.Name);
                return new(NotificationOutcome.Unread, bank);
            }

            var capture = _store.AddCaptured(parsed, settings.AutoCategorize);
            if (capture.Kind == CaptureKind.Duplicate) return new(NotificationOutcome.Duplicate, bank, capture);
            _logger.LogInformation("Wallet: saved a {Bank} transaction from a notification", bank.Name);
            Captured?.Invoke(this, capture);
            return new(NotificationOutcome.Added, bank, capture);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wallet: could not handle a notification");
            return new(NotificationOutcome.Ignored);
        }
    }

    /// <summary>A notification that talks about money (a balance, an amount in đồng) rather than news or an advert.</summary>
    private static bool LooksLikeMoney(string? title, string? text)
    {
        var folded = WalletText.Fold(WalletText.Clean(title + "\n" + text));
        if (!folded.Any(char.IsAsciiDigit)) return false;
        return folded.Contains("so du", StringComparison.Ordinal) || folded.Contains("vnd", StringComparison.Ordinal)
            || folded.Contains("so tien", StringComparison.Ordinal) || folded.Contains("bien dong", StringComparison.Ordinal)
            || folded.Contains(" sd", StringComparison.Ordinal);
    }

    private void KeepUnread(BankSource bank, string? title, string? text, DateTimeOffset at)
    {
        var entry = new UnreadNotification
        {
            At = at,
            Bank = bank.Name,
            Title = WalletLimits.Clip(title, 200),
            Text = WalletLimits.Clip(text, WalletLimits.Original),
        };
        _settings.Update(s =>
        {
            // The same notification shown again is kept once.
            s.Unread.RemoveAll(u => u.Text == entry.Text && u.Title == entry.Title);
            s.Unread.Insert(0, entry);
            if (s.Unread.Count > MaxUnread) s.Unread.RemoveRange(MaxUnread, s.Unread.Count - MaxUnread);
        });
    }

    public void ClearUnread() => _settings.Update(s => s.Unread.Clear());

    /// <summary>"Try a notification" on the settings page: what Helm reads in a pasted text, in a few lines.</summary>
    public static (bool Ok, string Text) Try(BankSource bank, string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "");
        if (BankNotificationParser.HasOneTimeCode("", text))
            return (false, "This looks like a one-time code (OTP). Helm never reads or keeps those.");
        var parsed = BankNotificationParser.Parse(bank.Name, "", text, now, zone);
        if (parsed is null)
            return (false, "Helm cannot read a transaction in this text: it needs a signed amount (like -150,000 or +5,000,000, or an amount with “ghi nợ / ghi có”) and a balance or an account.");
        var lines = new List<string>
        {
            $"{(parsed.Amount < 0 ? "Money out" : "Money in")}: {WalletFormat.Money(parsed.Amount)}",
            $"Balance after: {(parsed.Balance is { } b ? WalletFormat.Money(b) : "—")}",
            $"Account: {(parsed.Account.Length > 0 ? parsed.Account : "—")}",
            $"Description: {(parsed.Description.Length > 0 ? parsed.Description : "—")}",
            $"When: {TimeZoneInfo.ConvertTime(parsed.OccurredAt, zone):g}",
        };
        return (true, string.Join("\n", lines));
    }
}
