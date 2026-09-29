using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.Core.Platform;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes on Android. What is typed is saved when Helm goes to the background (and a moment after typing anyway), and
/// Back closes an open note before leaving the page.
/// </summary>
public sealed class NotesModule(NotesViewModel viewModel) : AndroidModuleBase, IModuleContent, IBackHandler
{
    public override string Id => NotesIds.ModuleId;
    public override string DisplayName => NotesIds.DisplayName;
    public override string Description => NotesIds.Description;
    public override ModuleGroup Group => ModuleGroup.SystemTools;
    public override Symbol Icon => Symbol.Notepad;

    /// <summary>The same vector icon as on Windows (<see cref="NotesIconShape"/>).</summary>
    public override IImage? IconImage => NotesIcon.Image;

    public override Type PageType => typeof(NotesPage);
    public Type ContentPageType => typeof(NotesContentPage);

    public override Task EnableAsync(CancellationToken ct)
    {
        StatusMessage = null;
        ActivityHost.Paused -= OnPaused;
        ActivityHost.Paused += OnPaused;
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        ActivityHost.Paused -= OnPaused;
        viewModel.Flush();
        return Task.CompletedTask;
    }

    /// <summary>Android back: an open note closes (saved) before the page does.</summary>
    public bool HandleBack()
    {
        if (!viewModel.IsEditorOpen) return false;
        viewModel.CloseEditor();
        return true;
    }

    // Another app, the home screen or the screen turning off: save now, the process may be killed later.
    private void OnPaused(object? sender, EventArgs e) => viewModel.Flush();
}
