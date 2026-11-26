using System.Windows.Controls;
using Helm.BugReports.Ui;
using Helm.Shell.BugReports;

namespace Helm.Shell.Windows;

/// <summary>
/// The Bug reports page: the shared Avalonia <see cref="BugReportsView"/> inside this WPF page. Reporting opens the
/// report window (<see cref="IPcBugReporting"/>); pictures come from this PC's file dialog and clipboard.
/// </summary>
public partial class BugReportsPage : Page
{
    public BugReportsPage(BugReportsViewModel viewModel, IPcBugReporting reporting)
    {
        InitializeComponent();
        Host.Show(() => new BugReportsView(viewModel, new PcBugReportsPageHost(reporting)));
    }
}
