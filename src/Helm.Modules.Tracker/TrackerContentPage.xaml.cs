using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Ui;
using Helm.Modules.Tracker.Ui;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the menu and the Quick access tile. SPIKE: a WPF shell around the shared Avalonia
/// <see cref="TrackerView"/>; this class only does what Windows does its own way (the save dialog).
/// </summary>
public partial class TrackerContentPage : Page, ITrackerPageHost
{
    private readonly TrackerViewModel _viewModel;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Host.Page = new PageFrame("Tracker", new TrackerView(module, viewModel, this));
        // "Added just now" goes stale after a minute; refresh the relative times while the page is on screen.
        _clock.Tick += (_, _) => viewModel.RefreshDetails();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) => _clock.Stop();
    }

    public void Opened() { }

    public void ExportCalendar(string ics)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"helm-tracker-{DateTime.Now:yyyyMMdd}.ics",
            DefaultExt = ".ics",
            Filter = "Calendar files (*.ics)|*.ics|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ics, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _viewModel.ReportExport("The calendar", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ReportExportFailure(ex);
        }
    }
}
