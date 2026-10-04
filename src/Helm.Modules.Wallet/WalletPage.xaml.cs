using System.Text;
using System.Windows;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Wallet;

/// <summary>Wallet's settings (Home → Utilities). The transactions themselves are on <see cref="WalletContentPage"/>.</summary>
public partial class WalletPage : ModulePageBase
{
    private readonly WalletViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public WalletPage(WalletModule module, WalletViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Android and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(WalletContentPage));

    /// <summary>View glue: the save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"helm-wallet-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            // UTF-8 with a BOM, so Excel shows Vietnamese text correctly.
            File.WriteAllText(dialog.FileName, _viewModel.Csv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _viewModel.ReportExport(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
