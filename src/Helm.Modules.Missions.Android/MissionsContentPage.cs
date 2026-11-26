using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core;
using Helm.Core.Platform;
using Helm.Core.Services;
using Helm.Core.Ui;
using Helm.Modules.Missions.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions, opened from the drawer, the Quick access tile and Home → Utilities (see <see cref="IModuleContent"/>): the
/// shared <see cref="MissionsView"/>, the same code as on Windows, filling the page area (<see cref="IFullPage"/>), plus
/// what only Android does: the notification permission, the CSV through the share sheet and the home-screen widget card.
/// Coming back to the app on it refreshes the missions; Back closes its sheet first (see <see cref="MissionsModule.HandleBack"/>).
/// </summary>
public sealed class MissionsContentPage : UserControl, IFullPage, IMissionsPageHost
{
    private readonly MissionsModule _module;
    private readonly MissionsViewModel _viewModel;
    private readonly MissionsView _view;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public MissionsContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<MissionsModule>(), HelmAndroidServices.Current.GetRequiredService<MissionsViewModel>())
    {
    }

    public MissionsContentPage(MissionsModule module, MissionsViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        _view = new MissionsView(module, viewModel, this);
        Content = _view;
    }

    public Action<Control>? OpenMenu => ShellChrome.OpenNavigation;

    public string ExportLabel => "Share CSV";

    /// <summary>Asks for notifications (Android 13+, once) and catches today's reminder.</summary>
    public void Opened() => _module.PageShown();

    public Control? CreatePlatformSettings() => new MissionsWidgetCard(_module);

    /// <summary>Every step as a CSV file through the share sheet (Drive, mail, a spreadsheet app…).</summary>
    public void ExportCsv(string csv)
    {
        try
        {
            var sharer = HelmAndroidServices.Current.GetRequiredService<IFileSharer>();
            Directory.CreateDirectory(sharer.ShareFolder);
            var path = Path.Combine(sharer.ShareFolder, $"helm-missions-{DateTime.Now:yyyyMMdd}.csv");
            // UTF-8 with a BOM, so spreadsheets show Vietnamese and other non-ASCII text correctly.
            File.WriteAllText(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            sharer.ShareFile(path, "text/csv", "Share the missions");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }

    // Pages are transient: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActivityHost.Resumed += OnResumed;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActivityHost.Resumed -= OnResumed;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnResumed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_view.Shown);
}
