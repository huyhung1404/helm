using System.Text;
using System.Windows;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Tracker;

public partial class TrackerPage : ModulePageBase
{
    private readonly TrackerViewModel _viewModel;

    public TrackerPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void ExportItems_Click(object sender, RoutedEventArgs e) => Export("Items", "helm-tracker-items", _viewModel.ItemsCsv);

    private void ExportHistory_Click(object sender, RoutedEventArgs e) => Export("History", "helm-tracker-history", _viewModel.HistoryCsv);

    /// <summary>View glue: the save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    private void Export(string what, string baseName, Func<string> csv)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{baseName}-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            // UTF-8 with a BOM, so Excel shows Vietnamese and other non-ASCII text correctly.
            File.WriteAllText(dialog.FileName, csv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _viewModel.ReportExport(what, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
