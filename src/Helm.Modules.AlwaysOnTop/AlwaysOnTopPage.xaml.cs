using Helm.Core.Ui;

namespace Helm.Modules.AlwaysOnTop;

public partial class AlwaysOnTopPage : ModulePageBase
{
    public AlwaysOnTopPage(AlwaysOnTopViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
