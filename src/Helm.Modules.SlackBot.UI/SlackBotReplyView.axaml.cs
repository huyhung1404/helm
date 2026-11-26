using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Helm.Modules.SlackBot.Ui;

/// <summary>The reply sheet (see <see cref="SlackBotView"/>): Enter sends, Shift+Enter starts a new line.</summary>
public partial class SlackBotReplyView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public SlackBotReplyView() => InitializeComponent();

    public SlackBotReplyView(SlackBotInboxViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Before the box itself: it would take Enter as a new line.
        ReplyBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            viewModel.SendReplyCommand.Execute(null);
        }, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => ReplyBox.Focus(), DispatcherPriority.Input);
    }
}
