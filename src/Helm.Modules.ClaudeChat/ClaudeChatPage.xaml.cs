using Helm.Core.Ui;
using Helm.Modules.ClaudeChat.Chat;

namespace Helm.Modules.ClaudeChat;

public partial class ClaudeChatPage : ModulePageBase
{
    public ClaudeChatPage(ClaudeChatViewModel viewModel, ClaudeChatView chatView)
    {
        DataContext = viewModel;
        InitializeComponent();
        chatView.DataContext = viewModel.Chat;
        viewModel.Presenter.AttachPageHost(ChatHost);
    }
}
