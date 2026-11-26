using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Modules.Wallet.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet on Windows. The transactions come from the phone (it reads the banks' notifications) through Helm Sync; the
/// PC shows them, categorizes them and adds cash by hand. Nothing runs in the background. The page is the shared
/// <see cref="WalletView"/>; its settings are a sheet inside it.
/// </summary>
public sealed class WalletModule(IServiceProvider services) : HelmModuleBase
{
    public override string Id => WalletIds.ModuleId;
    public override string DisplayName => WalletIds.DisplayName;
    public override string Description => WalletIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.Wallet;
    public override IImage IconImage => WalletIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Control CreatePage()
    {
        // Resolved here, not in the constructor: the view model loads the store, which can wait for the first open.
        var viewModel = services.GetRequiredService<WalletViewModel>();
        var host = new WindowsWalletPageHost(viewModel);
        var view = new WalletView(this, viewModel, host);
        host.View = view;
        return view;
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;
}
