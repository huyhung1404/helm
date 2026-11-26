using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH on Windows. Sessions live in <see cref="SshViewModel"/>; turning the tool off (or Helm exiting or updating)
/// closes them. The menu, Quick access and Home → Utilities open <see cref="SshContentPage"/>; its settings are a sheet inside it.
/// </summary>
public sealed class SshModule(SshViewModel viewModel) : HelmModuleBase, IModuleContent
{
    public override string Id => SshIds.ModuleId;
    public override string DisplayName => SshIds.DisplayName;
    public override string Description => SshIds.Description;
    public override ModuleGroup Group => ModuleGroup.SecurityAndServers;
    public override SymbolRegular Icon => SymbolRegular.WindowConsole20;
    public override ImageSource IconImage => SshLogo.Image;
    public override Type SettingsPageType => typeof(SshContentPage);
    public Type ContentPageType => typeof(SshContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        viewModel.SetEnabled(true);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        viewModel.SetEnabled(false);
        return Task.CompletedTask;
    }
}
