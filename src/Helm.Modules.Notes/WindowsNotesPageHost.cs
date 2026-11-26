using Helm.Modules.Notes.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.Notes;

/// <summary>
/// What only Windows does around the shared <see cref="NotesView"/>: the folder picker of the Markdown export, and
/// leaving the page saves what was typed.
/// </summary>
internal sealed class WindowsNotesPageHost(NotesViewModel viewModel) : INotesPageHost
{
    private NotesView? _view;

    public NotesView Attach(NotesView view)
    {
        _view = view;
        // Leaving the page (its tab closed or moved away) saves what was typed.
        view.DetachedFromVisualTree += (_, _) => viewModel.Flush();
        return view;
    }

    public bool CanExport => true;

    /// <summary>The folder picker is Windows UI; the files come from the shared view model.</summary>
    public async void Export()
    {
        try
        {
            var folder = await DesktopWindows.PickFolderAsync("Save the notes as Markdown files in", _view);
            if (folder is null) return;
            viewModel.ReportExport(viewModel.ExportMarkdown(folder), folder);
        }
        catch (Exception ex)
        {
            // async void: an IO error (or a picker that failed) is told on the page, never thrown.
            viewModel.ReportExportFailure(ex);
        }
    }
}
