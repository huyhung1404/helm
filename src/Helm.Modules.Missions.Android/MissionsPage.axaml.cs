using Avalonia.Interactivity;
using Helm.Core;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

/// <summary>Missions' settings (Home → Utilities). The missions themselves are on <see cref="MissionsContentPage"/>.</summary>
public partial class MissionsPage : ModulePageBase
{
    private readonly MissionsModule _module;
    private readonly IShellNavigation? _navigation;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public MissionsPage()
        : this(HelmAndroidServices.Current.GetRequiredService<MissionsModule>(), HelmAndroidServices.Current.GetRequiredService<MissionsViewModel>(),
            HelmAndroidServices.Current.GetRequiredService<IShellNavigation>())
    {
    }

    public MissionsPage(MissionsModule module, MissionsViewModel viewModel, IShellNavigation navigation)
    {
        _module = module;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is shared with Windows and knows nothing about the Android module type.
        Module = module;
        // Some launchers cannot place a widget for an app; they still list it in their own widget picker.
        AddWidgetButton.IsVisible = module.CanPinWidget;
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => _navigation?.ShowPage(typeof(MissionsContentPage));

    private void AddWidget_Click(object? sender, RoutedEventArgs e) => _module.PinWidget();

    /// <summary>Every step as a CSV file through the share sheet (Drive, mail, a spreadsheet app…).</summary>
    private void ShareCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MissionsViewModel viewModel) return;
        try
        {
            var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
            Directory.CreateDirectory(sharer.ShareFolder);
            var path = Path.Combine(sharer.ShareFolder, $"helm-missions-{DateTime.Now:yyyyMMdd}.csv");
            // UTF-8 with a BOM, so spreadsheets show Vietnamese and other non-ASCII text correctly.
            File.WriteAllText(path, viewModel.Csv(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            sharer.ShareFile(path, "text/csv", "Share the missions");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            viewModel.ReportExportFailure(ex);
        }
    }
}
