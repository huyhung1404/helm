using Helm.Core.Ui;

namespace Helm.Modules.Keyboard;

public partial class KeyboardPage : ModulePageBase
{
    public KeyboardPage(KeyboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
