using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Ui;
using Helm.Modules.Wallet.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Wallet itself, opened from the drawer, the Quick access tile, the Goal widget and the "what was it?" notification
/// (see <see cref="IModuleContent"/>): the shared <see cref="WalletView"/>, the same code as on Windows, filling the page
/// area (<see cref="IFullPage"/>). What is Android only is here: sharing the CSV, the drawer button, and reacting to the
/// app coming back (notification access, the bank's app) and to a tap on the Goal widget. Back closes its sheet first
/// (see <see cref="WalletModule.HandleBack"/>). The widgets' redraw follows the data in <see cref="WalletModule"/>.
/// </summary>
public sealed class WalletContentPage : UserControl, IFullPage, IWalletPageHost
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;
    private readonly WalletView _view;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WalletContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<WalletModule>(), HelmAndroidServices.Current.GetRequiredService<WalletViewModel>())
    {
    }

    public WalletContentPage(WalletModule module, WalletViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        _view = new WalletView(module, viewModel, this);
        Content = _view;
    }


    public string ExportText => "Share CSV";

    public string QuickCaptureHint =>
        "In Quick Capture (Share → Save to Helm, or the Quick Settings tile), type /s 50k coffee for money out or /s +2tr bonus for money in.";

    /// <summary>
    /// Asks for notifications (Android 13+, once), reads notification access again and wakes the listener if Android let
    /// it go.
    /// </summary>
    public void Opened() => _module.PageShown();

    /// <summary>Every transaction as a CSV file through the share sheet (Drive, mail, a spreadsheet app…).</summary>
    public void ExportCsv(string csv)
    {
        var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
        Directory.CreateDirectory(sharer.ShareFolder);
        var path = Path.Combine(sharer.ShareFolder, $"helm-wallet-{DateTime.Now:yyyyMMdd}.csv");
        // UTF-8 with a BOM, so spreadsheets show Vietnamese text correctly.
        File.WriteAllText(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        sharer.ShareFile(path, "text/csv", "Share the transactions");
    }

    // Pages are transient: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
        ShellIntents.ItemOffered += OnItemOffered;
        Dispatcher.UIThread.Post(OpenOfferedGoal);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        ShellIntents.ItemOffered -= OnItemOffered;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnItemOffered(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OpenOfferedGoal);

    /// <summary>A tap on the Goal widget: that goal's sheet, or the goals when there is none yet.</summary>
    private void OpenOfferedGoal()
    {
        if (ShellIntents.TakeItem(WalletIds.ModuleId) is not { } item || !item.StartsWith(GoalWidgets.ItemPrefix, StringComparison.Ordinal)) return;
        var id = item[GoalWidgets.ItemPrefix.Length..];
        if (id.Length > 0) _viewModel.ShowGoal(id);
        else _viewModel.ShowGoalsCommand.Execute(null);
    }

    /// <summary>Back from Android's settings (notification access) or from the bank's app.</summary>
    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_view.Shown);
}
