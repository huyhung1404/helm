using System.Text;
using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Missions;

/// <summary>Missions' settings (Home → Utilities). The missions themselves are on <see cref="MissionsContentPage"/>.</summary>
public partial class MissionsPage : ModulePageBase
{
    private readonly MissionsViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public MissionsPage(MissionsModule module, MissionsViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(MissionsContentPage));

    /// <summary>View glue: the save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"helm-missions-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            // UTF-8 with a BOM, so Excel shows Vietnamese and other non-ASCII text correctly.
            File.WriteAllText(dialog.FileName, _viewModel.Csv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _viewModel.ReportExport(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
