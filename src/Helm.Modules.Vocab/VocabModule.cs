using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Modules.Vocab.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary on Windows. The words live in Helm Sync and the pages work on <see cref="VocabStore"/>; the background
/// work is the pop-up card that shows a word now and then (<see cref="VocabPopup"/>). The page is the shared
/// <see cref="VocabView"/>; the settings are a sheet inside it.
/// </summary>
public sealed class VocabModule(VocabPopup popup, IServiceProvider services) : HelmModuleBase
{
    public override string Id => VocabIds.ModuleId;
    public override string DisplayName => VocabIds.DisplayName;
    public override string Description => VocabIds.Description;
    public override ModuleGroup Group => ModuleGroup.Learning;
    public override Symbol Icon => Symbol.BookLetter;
    public override IImage IconImage => VocabIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Control CreatePage()
    {
        var viewModel = services.GetRequiredService<VocabViewModel>();
        return new VocabView(this, viewModel, new WindowsVocabPageHost(popup, viewModel));
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        popup.Start();
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        popup.Stop();
        return Task.CompletedTask;
    }
}
