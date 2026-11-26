using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Helm.Modules.Wallet.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.Wallet;

/// <summary>
/// What the shared <see cref="WalletView"/> needs from Windows: the save dialog of the CSV export (the CSV itself comes
/// from the shared view model). Its settings are the sheet behind the gear.
/// </summary>
internal sealed class WindowsWalletPageHost(WalletViewModel viewModel) : IWalletPageHost
{
    private static readonly FilePickerFileType Csv = new("CSV files") { Patterns = ["*.csv"] };

    /// <summary>The view this host serves (set right after the view is made): the dialog's owner.</summary>
    public Control? View { get; set; }

    public void Opened()
    {
    }

    public string QuickCaptureHint => "In Quick Capture (Win+Alt+N), type /s 50k coffee for money out or /s +2tr bonus for money in.";

    public void ExportCsv(string csv) => _ = ExportCsvAsync(csv);

    private async Task ExportCsvAsync(string csv)
    {
        var path = await DesktopWindows.PickSaveAsync("Export transactions", $"helm-wallet-{DateTime.Now:yyyyMMdd}.csv",
            [Csv, FilePickerFileTypes.All], View);
        if (path is null) return;
        try
        {
            // UTF-8 with a BOM, so Excel shows Vietnamese text correctly.
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            viewModel.ReportExport(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            viewModel.ReportExportFailure(ex);
        }
    }
}
