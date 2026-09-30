using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the
/// tool is off the lists stay visible but read-only, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class TrackerContentPage : Page
{
    private readonly TrackerModule _module;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(60) };

    public TrackerContentPage(TrackerModule module, TrackerViewModel viewModel)
    {
        _module = module;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
        // "Added just now" goes stale after a minute; refresh the relative times while the page is on screen.
        _clock.Tick += (_, _) => viewModel.RefreshDetails();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) => _clock.Stop();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Body.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>View glue: the save dialog is Windows UI; the calendar file comes from the shared view model.</summary>
    private void ExportIcs_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (TrackerViewModel)DataContext;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"helm-tracker-{DateTime.Now:yyyyMMdd}.ics",
            DefaultExt = ".ics",
            Filter = "Calendar files (*.ics)|*.ics|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, viewModel.CalendarIcs(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            viewModel.ReportExport("The calendar", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            viewModel.ReportExportFailure(ex);
        }
    }

    private bool _grouping;

    /// <summary>Thousands separators while an amount is typed, so 1500000 reads 1,500,000; the caret stays put.</summary>
    private void OnAmountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_grouping || sender is not TextBox box) return;
        var (text, caret) = TrackerFormat.GroupDigits(box.Text, box.CaretIndex);
        if (text == box.Text) return;
        _grouping = true;
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            _grouping = false;
        }
    }
}
