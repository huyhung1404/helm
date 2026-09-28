using System.Text;
using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Tracker;

/// <summary>Tracker's settings (Home → Utilities). The lists are on <see cref="TrackerContentPage"/>.</summary>
public partial class TrackerPage : ModulePageBase
{
    private readonly TrackerViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public TrackerPage(TrackerModule module, TrackerViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void OpenTracker_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(TrackerContentPage));

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
