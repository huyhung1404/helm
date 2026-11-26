using System.Text;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Helm.Modules.Tracker.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.Tracker;

/// <summary>
/// What only Windows does around the shared <see cref="TrackerView"/>: the save dialog of the calendar export, and the
/// relative times ("Added just now") refreshed every minute while the page is on screen.
/// </summary>
internal sealed class WindowsTrackerPageHost : ITrackerPageHost
{
    private static readonly FilePickerFileType[] CalendarTypes =
    [
        new("Calendar files") { Patterns = ["*.ics"], MimeTypes = ["text/calendar"] },
        FilePickerFileTypes.All,
    ];

    private readonly TrackerViewModel _viewModel;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };
    private TrackerView? _view;

    public WindowsTrackerPageHost(TrackerViewModel viewModel)
    {
        _viewModel = viewModel;
        _clock.Tick += (_, _) => viewModel.RefreshDetails();
    }

    public TrackerView Attach(TrackerView view)
    {
        _view = view;
        view.AttachedToVisualTree += (_, _) => _clock.Start();
        view.DetachedFromVisualTree += (_, _) => _clock.Stop();
        return view;
    }

    public void Opened() { }

    /// <summary>The save dialog is Windows UI; the calendar file comes from the shared view model.</summary>
    public async void ExportCalendar(string ics)
    {
        try
        {
            var path = await DesktopWindows.PickSaveAsync("Export calendar", $"helm-tracker-{DateTime.Now:yyyyMMdd}.ics", CalendarTypes, _view);
            if (path is null) return;
            await File.WriteAllTextAsync(path, ics, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _viewModel.ReportExport("The calendar", path);
        }
        catch (Exception ex)
        {
            // async void: an IO error (or a picker that failed) is told on the page, never thrown.
            _viewModel.ReportExportFailure(ex);
        }
    }
}
