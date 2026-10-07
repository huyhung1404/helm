using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet on Android. <see cref="BankNotificationListener"/> reads the banks' notifications (once the user grants
/// notification access) and saves their transactions, with or without the activity; the module keeps the home-screen
/// widgets in step with the data. Back closes the add panel before leaving the page.
/// </summary>
public sealed class WalletModule : AndroidModuleBase, IModuleContent, IBackHandler, IDisposable
{
    private readonly WalletViewModel _viewModel;
    private readonly ILogger<WalletModule> _logger;
    private readonly Timer _widgetTimer;

    public WalletModule(WalletViewModel viewModel, WalletStore store, ILogger<WalletModule> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        _widgetTimer = new Timer(_ => RefreshWidgets(), null, Timeout.Infinite, Timeout.Infinite);
        // A sync can change many records in a row: redraw the widgets once it has settled.
        store.Changed += (_, _) => _widgetTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _widgetTimer.Dispose();

    public override string Id => WalletIds.ModuleId;
    public override string DisplayName => WalletIds.DisplayName;
    public override string Description => WalletIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.Wallet;

    /// <summary>The same vector icon as on Windows (<see cref="WalletIconShape"/>).</summary>
    public override IImage? IconImage => WalletIcon.Image;

    public override Type PageType => typeof(WalletPage);
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

    /// <summary>Android back: an open add panel closes before the page does.</summary>
    public bool HandleBack()
    {
        if (!_viewModel.IsAddOpen) return false;
        _viewModel.CancelAddCommand.Execute(null);
        return true;
    }
}
