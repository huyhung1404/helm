using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Notes;

/// <summary>Notes' settings (Home → Utilities). The notes are on <see cref="NotesContentPage"/>.</summary>
public partial class NotesPage : ModulePageBase
{
    private readonly NotesViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public NotesPage(NotesModule module, NotesViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenNotes_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(NotesContentPage));

    /// <summary>View glue: the folder dialog is Windows UI; the files come from the shared view model.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
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
