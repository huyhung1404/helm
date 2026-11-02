using Helm.Core.Ui;

namespace Helm.Modules.WeeklyReport;

public partial class WeeklyReportPage : ModulePageBase
{
    public WeeklyReportPage(WeeklyReportSettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
