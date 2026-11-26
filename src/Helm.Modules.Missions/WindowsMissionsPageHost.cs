using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Helm.Modules.Missions.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.Missions;

/// <summary>
/// What only Windows does around the shared <see cref="MissionsView"/>: the save dialog of the CSV export, and coming
/// back to Helm while the page shows refreshes the missions (steps done on another device or by an AI agent meanwhile).
/// </summary>
internal sealed class WindowsMissionsPageHost(MissionsViewModel viewModel) : IMissionsPageHost
{
    private static readonly FilePickerFileType[] CsvTypes =
    [
        new("CSV files") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] },
        FilePickerFileTypes.All,
    ];

    private MissionsView? _view;
    private Window? _window;

    /// <summary>Follows the window <paramref name="view"/> is in (the tab may move between windows).</summary>
    public MissionsView Attach(MissionsView view)
    {
        _view = view;
        view.AttachedToVisualTree += (_, _) => Watch(TopLevel.GetTopLevel(view) as Window);
        view.DetachedFromVisualTree += (_, _) => Watch(null);
        return view;
    }

    public void Opened()
    {
    }

    /// <summary>View glue: the save dialog is Windows UI; the CSV itself comes from the shared view model.</summary>
    public async void ExportCsv(string csv)
    {
        try
        {
            var path = await DesktopWindows.PickSaveAsync("Export CSV", $"helm-missions-{DateTime.Now:yyyyMMdd}.csv", CsvTypes, _view);
            if (path is null) return;
            // UTF-8 with a BOM, so Excel shows Vietnamese and other non-ASCII text correctly.
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            viewModel.ReportExport(path);
        }
        catch (Exception ex)
        {
            // async void: an IO error (or a picker that failed) is told on the page, never thrown.
            viewModel.ReportExportFailure(ex);
        }
    }

    private void Watch(Window? window)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = window;
        if (_window is not null) _window.Activated += OnWindowActivated;
    }

    /// <summary>Back at Helm after a while (the page was left open): steps done elsewhere meanwhile.</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => _view?.Shown();
}
