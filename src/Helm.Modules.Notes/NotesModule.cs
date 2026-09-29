using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes on Windows. The notes live in Helm Sync and the pages work directly on <see cref="NotesStore"/>; there is no
/// background work. The menu and Quick access open <see cref="NotesContentPage"/>; Home → Utilities opens the settings
/// (<see cref="NotesPage"/>).
/// </summary>
public sealed class NotesModule(NotesViewModel viewModel) : HelmModuleBase, IModuleContent
{
    public override string Id => NotesIds.ModuleId;
    public override string DisplayName => NotesIds.DisplayName;
    public override string Description => NotesIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override SymbolRegular Icon => SymbolRegular.Notepad24;
    public override ImageSource IconImage => NotesLogo.Image;
    public override Type SettingsPageType => typeof(NotesPage);
    public Type ContentPageType => typeof(NotesContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: whatever is typed is saved first.</summary>
    public override Task DisableAsync()
    {
        viewModel.Flush();
        return Task.CompletedTask;
    }
}
