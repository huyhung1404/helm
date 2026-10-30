using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary on Windows. The words live in Helm Sync and the pages work on <see cref="VocabStore"/>; the background
/// work is the pop-up card that shows a word now and then (<see cref="VocabPopup"/>) and Claude Code breaking down new
/// words by itself (<see cref="VocabAgentViewModel"/>, when its setting is on). The menu and Quick access open
/// <see cref="VocabContentPage"/>; Home → Utilities opens the settings (<see cref="VocabPage"/>).
/// </summary>
public sealed class VocabModule(VocabPopup popup, VocabAgentViewModel agent) : HelmModuleBase, IModuleContent
{
    public override string Id => VocabIds.ModuleId;
    public override string DisplayName => VocabIds.DisplayName;
    public override string Description => VocabIds.Description;
    public override ModuleGroup Group => ModuleGroup.Learning;
    public override SymbolRegular Icon => SymbolRegular.BookLetter24;
    public override ImageSource IconImage => VocabLogo.Image;
    public override Type SettingsPageType => typeof(VocabPage);
    public Type ContentPageType => typeof(VocabContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        popup.Start();
        agent.Watch(true);
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        popup.Stop();
        agent.Watch(false);
        return Task.CompletedTask;
    }
}
