using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.Core.Modules;

namespace Helm.Modules.WeeklyReport;

/// <summary>
/// Weekly Report itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off, the past reports stay visible but the page is read-only.
/// </summary>
public partial class WeeklyReportContentPage : Page
{
    private readonly WeeklyReportModule _module;
    private readonly WeeklyReportViewModel _viewModel;

    public WeeklyReportContentPage(WeeklyReportModule module, WeeklyReportViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Module and page are both singletons, so the subscription lives as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
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

    /// <summary>Copies the report as it is in the box (edited by hand or not) and keeps it with the past reports.</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DraftBox.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy; the button can be pressed again.
            return;
        }
        _viewModel.Finished(DraftBox.Text);
    }

    private void Request_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.ReviseCommand.CanExecute(null)) return;
        e.Handled = true;
        _viewModel.ReviseCommand.Execute(null);
    }
}
