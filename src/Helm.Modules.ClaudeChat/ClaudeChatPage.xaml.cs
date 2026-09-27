using Helm.Core.Ui;

namespace Helm.Modules.ClaudeChat;

public partial class ClaudeChatPage : ModulePageBase
{
    public ClaudeChatPage(ClaudeChatViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
