using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Wpf.Ui.Controls;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader on Windows. Nothing runs in the background: the dictionaries load when a novel is opened and are freed
/// when the tool is turned off. The menu and Quick access open <see cref="NovelReaderContentPage"/>; Home → Utilities
/// opens the settings (<see cref="NovelReaderPage"/>).
/// </summary>
public sealed class NovelReaderModule(NovelReaderViewModel viewModel, IUiDispatcher ui, VieNeuServer localVoice, EdgeAnchor edge) : HelmModuleBase, IModuleContent
{
    public override string Id => NovelReaderIds.ModuleId;
    public override string DisplayName => NovelReaderIds.DisplayName;
    public override string Description => NovelReaderIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override SymbolRegular Icon => SymbolRegular.BookOpen24;
    public override ImageSource IconImage => NovelReaderLogo.Image;
    public override Type SettingsPageType => typeof(NovelReaderPage);
    public Type ContentPageType => typeof(NovelReaderContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: reading aloud stops, the place reading stopped is saved, and the local voice server and the hidden Edge stop.</summary>
    public override async Task DisableAsync()
    {
        await ui.InvokeAsync(viewModel.Release).ConfigureAwait(false);
        localVoice.Stop();
        edge.Stop();
    }
}
