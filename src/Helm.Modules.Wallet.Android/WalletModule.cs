using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet on Android. <see cref="BankNotificationListener"/> reads the banks' notifications (once the user grants
/// notification access) and saves their transactions, with or without the activity; the module keeps the home-screen
/// widgets in step with the data (and with the widget settings and the Goal widget's choice, which change on the page).
/// Back closes the open sheet before leaving the page.
/// </summary>
public sealed class WalletModule : AndroidModuleBase, IModuleContent, IBackHandler, IDisposable
{
    private readonly WalletViewModel _viewModel;
    private readonly WalletStore _store;
    private readonly FundBook _funds;
    private readonly ISettingsStore<WalletSettings> _settings;
    private readonly ILogger<WalletModule> _logger;
    private readonly Timer _widgetTimer;

    public WalletModule(WalletViewModel viewModel, WalletStore store, FundBook funds, GoalBook goals, ISettingsStoreFactory settings, ILogger<WalletModule> logger)
    {
        _viewModel = viewModel;
        _store = store;
        _funds = funds;
        _settings = settings.Get<WalletSettings>(WalletIds.ModuleId);
        _logger = logger;
        _widgetTimer = new Timer(_ => Settled(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change many records in a row: redraw the widgets and check the funds once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        funds.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        // Money set aside moves the balance and lights the Goal widget's photo.
        goals.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        // The widget's look (settings sheet) and another goal put on the Goal widget: redraw at once.
        viewModel.WidgetStyleChanged += (_, _) => RefreshWidgets();
        if (viewModel.Goals is { } goalsModel) goalsModel.WidgetChanged += (_, _) => RefreshWidgets();
    }

    public void Dispose() => _widgetTimer.Dispose();

    public override string Id => WalletIds.ModuleId;
    public override string DisplayName => WalletIds.DisplayName;
    public override string Description => WalletIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.Wallet;

    /// <summary>The same vector icon as on Windows (<see cref="WalletIconShape"/>).</summary>
    public override IImage? IconImage => Ui.WalletIcon.Image;

    public override Type PageType => typeof(WalletContentPage);
    public Type ContentPageType => typeof(WalletContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        RefreshWidgets();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        // They show "Wallet is turned off"; the listener stops saving while the tool is off.
        RefreshWidgets();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A Wallet page is on screen: the moment to ask for notifications (Android 13+, once; the "what was it?"
    /// notification needs them), to read notification access again and to wake the listener if Android let it go.
    /// </summary>
    public void PageShown()
    {
        try
        {
            var context = AndroidApp.Context;
            WalletNotifications.AskPermissionOnce(context);
            _viewModel.RefreshAccess();
            if (_viewModel.HasNotificationAccess) BankNotificationListener.Rebind(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wallet: could not check the notification access");
        }
    }

    private void Settled()
    {
        RefreshWidgets();
        WarnFunds();
    }

    /// <summary>A fund that got worse (past its warning share, used up, over) warns once per level and month.</summary>
    private void WarnFunds()
    {
        try
        {
            if (!IsEnabled || !_settings.Current.WarnFunds) return;
            var zone = TimeZoneInfo.Local;
            var now = _store.Now;
            var month = WalletFormat.MonthOf(now, zone);
            var states = _funds.States(_store.Transactions(), month, now, zone);
            IReadOnlyList<FundAlerts.Warning> due = [];
            _settings.Update(s => due = FundAlerts.Due(states, month, s.FundWarnings));
            foreach (var warning in due) WalletNotifications.WarnFund(AndroidApp.Context, warning);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not check the funds");
        }
    }

    private void RefreshWidgets()
    {
        try
        {
            WalletWidgets.RefreshAll(AndroidApp.Context);
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "Could not refresh the Wallet widgets");
        }
    }

    /// <summary>
    /// Android back: the sheet on top closes first (a pop-up goes one step back, to the transaction it edits or the list it
    /// was opened from, as a tap outside does); the page goes only when none is open.
    /// </summary>
    public bool HandleBack() => Helm.Core.Ui.Sheet.CloseTopmost();
}
