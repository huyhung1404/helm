using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions on Windows. The data lives in Helm Sync and the pages work on <see cref="MissionsStore"/>; nothing runs in
/// the background. The menu and Quick access open <see cref="MissionsContentPage"/>; Home → Utilities opens the
/// settings (<see cref="MissionsPage"/>).
/// </summary>
public sealed class MissionsModule : HelmModuleBase, IModuleContent
{
    public override string Id => MissionsIds.ModuleId;
    public override string DisplayName => MissionsIds.DisplayName;
    public override string Description => MissionsIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.Flag24;
    public override ImageSource IconImage => MissionsLogo.Image;
    public override Type SettingsPageType => typeof(MissionsPage);
    public Type ContentPageType => typeof(MissionsContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;
}
