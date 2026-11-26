using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Speech.VieNeu;
using Helm.Modules.NovelReader.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader on Windows. The dictionaries load when a novel is opened and are freed when the tool is turned off; in
/// the background only <see cref="NovelSourceWatcher"/> runs, looking for new chapters of the web novels that ask for it.
/// The page is the shared <see cref="NovelReaderView"/>; its settings are a sheet inside it.
/// </summary>
public sealed class NovelReaderModule(NovelReaderViewModel viewModel, IUiDispatcher ui, VieNeuSpeechEngine voice,
    NovelSourceWatcher watcher, IServiceProvider services) : HelmModuleBase
{
    public override string Id => NovelReaderIds.ModuleId;
    public override string DisplayName => NovelReaderIds.DisplayName;
    public override string Description => NovelReaderIds.Description;
    public override ModuleGroup Group => ModuleGroup.MoneyAndMedia;
    public override Symbol Icon => Symbol.BookOpen;
    public override IImage IconImage => NovelReaderIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Control CreatePage()
    {
        var host = new WindowsNovelReaderPageHost(services.GetRequiredService<WordClassDictionary>(), services.GetRequiredService<NovelScraperViewModel>());
        var view = new NovelReaderView(this, viewModel, host, services.GetRequiredService<IClipboardService>());
        host.View = view;
        return view;
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        watcher.Start();
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: reading aloud stops, the place reading stopped is saved, the VieNeu voice lets its model go and the look for new chapters stops.</summary>
    public override async Task DisableAsync()
    {
        watcher.Stop();
        await ui.InvokeAsync(viewModel.Release).ConfigureAwait(false);
        voice.Release();
    }
}
