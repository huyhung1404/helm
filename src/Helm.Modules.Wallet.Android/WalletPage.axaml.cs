using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>Wallet's settings (Home → Utilities). The transactions themselves are on <see cref="WalletContentPage"/>.</summary>
public partial class WalletPage : ModulePageBase
{
    private readonly WalletModule _module;
    private readonly WalletViewModel _viewModel;
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public WalletPage()
        : this(HelmAndroidServices.Current.GetRequiredService<WalletModule>(), HelmAndroidServices.Current.GetRequiredService<WalletViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public WalletPage(WalletModule module, WalletViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        // Some launchers cannot place a widget for an app; they still list it in their own widget picker.
        AddWidgetButton.IsVisible = viewModel.CanPinWidget;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
        _viewModel.WidgetStyleChanged += OnWidgetStyleChanged;
        _module.PageShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        _viewModel.WidgetStyleChanged -= OnWidgetStyleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWidgetStyleChanged(object? sender, EventArgs e) => WalletWidgets.RefreshAll(Android.App.Application.Context);

    /// <summary>Back from Android's Notification access or App info: show whether access is on now.</summary>
    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_module.PageShown);

    private void Open_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(WalletContentPage));

    /// <summary>Every transaction as a CSV file through the share sheet (Drive, mail, a spreadsheet app…).</summary>
    private void ShareCsv_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
            Directory.CreateDirectory(sharer.ShareFolder);
            var path = Path.Combine(sharer.ShareFolder, $"helm-wallet-{DateTime.Now:yyyyMMdd}.csv");
            // UTF-8 with a BOM, so spreadsheets show Vietnamese text correctly.
            File.WriteAllText(path, _viewModel.Csv(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            sharer.ShareFile(path, "text/csv", "Share the transactions");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
