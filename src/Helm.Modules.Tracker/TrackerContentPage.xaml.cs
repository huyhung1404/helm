using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Services;
using Helm.Modules.Tracker.Ui;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>): the shared
/// Avalonia <see cref="TrackerView"/> inside this WPF page, plus what only Windows does (the save dialog, showing
/// the settings page).
/// </summary>
public partial class TrackerContentPage : Page, ITrackerPageHost
{
    private readonly TrackerViewModel _viewModel;
    private readonly IShellNavigation _navigation;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        InitializeComponent();
        Host.Show(() => new TrackerView(module, viewModel, this));
        // "Added just now" goes stale after a minute; refresh the relative times while the page is on screen.
        _clock.Tick += (_, _) => viewModel.RefreshDetails();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) => _clock.Stop();
    }

    public void Opened() { }

    public void OpenSettings() => _navigation.ShowPage(typeof(TrackerPage));

    /// <summary>The save dialog is Windows UI; the calendar file comes from the shared view model.</summary>
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
