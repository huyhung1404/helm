using Helm.Core.Ui;

namespace Helm.Modules.CommandPalette;

public partial class CommandPalettePage : ModulePageBase
{
    public CommandPalettePage(CommandPaletteViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
