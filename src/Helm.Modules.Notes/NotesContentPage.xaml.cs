using System.Windows;
using System.Windows.Controls;
using Helm.Modules.Notes.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="NotesView"/> inside this WPF page, plus what only Windows does (the folder dialog of the export).
/// </summary>
public partial class NotesContentPage : Page, INotesPageHost
{
    private readonly NotesViewModel _viewModel;

    public NotesContentPage(NotesModule module, NotesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Host.Show(() => new NotesView(module, viewModel, this));
        // Leaving the page saves what was typed.
        Unloaded += (_, _) => viewModel.Flush();
    }

    public bool CanExport => true;

    /// <summary>The folder dialog is Windows UI; the files come from the shared view model.</summary>
    public void Export()
    {
        var dialog = new OpenFolderDialog { Title = "Save the notes as Markdown files in" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _viewModel.ReportExport(_viewModel.ExportMarkdown(dialog.FolderName), dialog.FolderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
