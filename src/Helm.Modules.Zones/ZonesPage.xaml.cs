using Helm.Core.Ui;

namespace Helm.Modules.Zones;

public partial class ZonesPage : ModulePageBase
{
    public ZonesPage(ZonesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
