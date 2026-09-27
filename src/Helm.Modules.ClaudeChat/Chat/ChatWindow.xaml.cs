using System.Windows;
using System.Windows.Controls;
using Helm.Modules.ClaudeChat.Cli;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>The Claude Chat window: folders and their chats on the left, the selected chat on the right.</summary>
public partial class ChatWindow : FluentWindow
{
    public ChatWindow(ChatWorkspaceViewModel workspace)
    {
        DataContext = workspace;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplicationThemeManager.Apply(this);
        ChatView.Workspace = workspace;
    }

    public void FocusComposer() => ChatView.FocusComposer();

    /// <summary>Picking a chat row shows it; picking a folder row keeps the current chat.</summary>
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ChatViewModel chat && DataContext is ChatWorkspaceViewModel workspace)
        {
            workspace.SelectedChat = chat;
            Dispatcher.BeginInvoke(ChatView.FocusComposer);
        }
    }

    private async void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not PastSession past || DataContext is not ChatWorkspaceViewModel workspace) return;
        HistoryList.SelectedItem = null;
        await workspace.OpenPastCommand.ExecuteAsync(past);
    }
}
