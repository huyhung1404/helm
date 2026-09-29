using Helm.Core.Ui;

namespace Helm.Modules.QuickCapture;

public partial class QuickCapturePage : ModulePageBase
{
    public QuickCapturePage(QuickCaptureViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
