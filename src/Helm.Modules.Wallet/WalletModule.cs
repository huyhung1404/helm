using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet on Windows. The transactions come from the phone (it reads the banks' notifications) through Helm Sync; the
/// PC shows them, categorizes them and adds cash by hand. Nothing runs in the background. The menu and Quick access
/// open <see cref="WalletContentPage"/>; Home → Utilities opens the settings (<see cref="WalletPage"/>).
/// </summary>
public sealed class WalletModule : HelmModuleBase, IModuleContent
{
    public override string Id => WalletIds.ModuleId;
    public override string DisplayName => WalletIds.DisplayName;
    public override string Description => WalletIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override SymbolRegular Icon => SymbolRegular.Wallet24;
    public override ImageSource IconImage => WalletLogo.Image;
    public override Type SettingsPageType => typeof(WalletPage);
    public Type ContentPageType => typeof(WalletContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;
}
