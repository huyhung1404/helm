using System.Text;
using System.Windows;
using System.Windows.Controls;
using Helm.Modules.Missions.Ui;
using Microsoft.Win32;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions itself, opened from the menu, Quick access and Home → Utilities (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="MissionsView"/> inside this WPF page. Coming back to Helm while it shows refreshes the missions
/// (steps done on another device or by an AI agent meanwhile).
/// </summary>
public partial class MissionsContentPage : Page, IMissionsPageHost
{
    private readonly MissionsViewModel _viewModel;
    private MissionsView? _view;
    private Window? _window;

    public MissionsContentPage(MissionsModule module, MissionsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Host.Show(() => _view = new MissionsView(module, viewModel, this));
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.Activated += OnWindowActivated;
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
    }

    /// <summary>Back at Helm after a while (the page was left open): steps done elsewhere meanwhile.</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => _view?.Shown();

    public void Opened()
    {
    }

    /// <summary>View glue: the save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    public void ExportCsv(string csv)
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
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _viewModel.ReportExport(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
