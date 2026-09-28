using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker on Windows. It has nothing running in the background: the data lives in Helm Sync and the page works
/// directly on <see cref="TrackerStore"/>. Turning it off makes the lists read-only. The menu and Quick access open
/// <see cref="TrackerContentPage"/>; Home → Utilities opens the settings (<see cref="TrackerPage"/>).
/// </summary>
public sealed class TrackerModule : HelmModuleBase, IModuleContent
{
    public override string Id => TrackerIds.ModuleId;
    public override string DisplayName => TrackerIds.DisplayName;
    public override string Description => TrackerIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.TaskListSquareLtr24;
    public override Type SettingsPageType => typeof(TrackerPage);
    public Type ContentPageType => typeof(TrackerContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    public override Task DisableAsync() => Task.CompletedTask;
}
