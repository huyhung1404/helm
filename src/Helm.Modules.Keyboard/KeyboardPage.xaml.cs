using System.Windows.Controls;

namespace Helm.Modules.Keyboard;

public partial class KeyboardPage : Page
{
    public KeyboardPage(KeyboardViewModel viewModel)
    {
        InitializeComponent();
        Host.Show(() => new KeyboardView(viewModel));
    }
}
