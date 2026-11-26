using System.Text;
using System.Windows;
using System.Windows.Controls;
using Helm.Core.Modules;
using Helm.Modules.Wallet.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="WalletView"/> inside this WPF page. Its settings are the sheet behind the gear. What is Windows
/// only is here: the save dialog of the CSV export.
/// </summary>
public partial class WalletContentPage : Page, IWalletPageHost
{
    private readonly WalletViewModel _viewModel;

    public WalletContentPage(WalletModule module, WalletViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Host.Show(() => new WalletView(module, viewModel, this));
    }

    public void Opened()
    {
    }

    public string QuickCaptureHint => "In Quick Capture (Win+Alt+N), type /s 50k coffee for money out or /s +2tr bonus for money in.";

    /// <summary>The save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    public void ExportCsv(string csv)
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
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _viewModel.ReportExport(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
