using System.Windows.Controls;
using System.Windows.Input;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// The chat surface. DataContext is a <see cref="ChatViewModel"/>; one instance is re-parented between the module
/// page and a floating window, so it must not assume who hosts it.
/// </summary>
public partial class ClaudeChatView : UserControl
{
    private bool _followTail = true;

    public ClaudeChatView() => InitializeComponent();

    public void FocusComposer() => Composer.Focus();

    /// <summary>Enter sends, Shift+Enter inserts a line break.</summary>
    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    /// <summary>Stay pinned to the newest message while it streams, unless the user scrolled up to read.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only a pure scroll (content and viewport unchanged) says where the user wants to be. Growth of the
        // content or a resize of the viewport must not unpin the tail.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _followTail = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
            return;
        }
        if (_followTail) Scroller.ScrollToEnd();
    }
}
