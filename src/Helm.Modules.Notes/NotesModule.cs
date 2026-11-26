using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Modules.Notes.Ui;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes on Windows. The notes live in Helm Sync and the pages work directly on <see cref="NotesStore"/>; the only
/// background work is indexing them for search by meaning, where the model was downloaded (<see cref="NoteIndexer"/>).
/// The menu, Quick access and Home → Utilities open its page, the shared <see cref="NotesView"/>; its settings are a sheet inside it.
/// </summary>
public sealed class NotesModule(NotesViewModel viewModel, NoteIndexer indexer) : HelmModuleBase
{
    public override string Id => NotesIds.ModuleId;
    public override string DisplayName => NotesIds.DisplayName;
    public override string Description => NotesIds.Description;
    public override ModuleGroup Group => ModuleGroup.Planning;
    public override Symbol Icon => Symbol.Notepad;
    public override IImage IconImage => NotesIcon.Image;
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Control CreatePage()
    {
        var host = new WindowsNotesPageHost(viewModel);
        return host.Attach(new NotesView(this, viewModel, host));
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        indexer.Start();
        return Task.CompletedTask;
    }

    /// <summary>Also runs when Helm exits or updates: whatever is typed is saved first.</summary>
    public override Task DisableAsync()
    {
        viewModel.Flush();
        indexer.Stop();
        return Task.CompletedTask;
    }
}
