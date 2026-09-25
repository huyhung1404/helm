using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// The chat surface. DataContext is a <see cref="ChatViewModel"/>; one instance is re-parented between the module
/// page and a floating window by <see cref="ChatPresenter"/>, so it must not assume who hosts it.
/// </summary>
public partial class ClaudeChatView : UserControl
{
    /// <summary>How far (DIPs) the header must be dragged before the chat tears out of the page.</summary>
    internal const double TearOffDistance = 40;

    private bool _followTail = true;
    private Point? _pressedAt;

    public ClaudeChatView()
    {
        InitializeComponent();
        UpdateHeaderHint();
    }

    /// <summary>The header was dragged out while docked; the argument is where it was grabbed (relative to this view).</summary>
    public event EventHandler<Point>? TearOffRequested;

    /// <summary>The header was dragged while floating: move the window.</summary>
    public event EventHandler? WindowDragRequested;

    /// <summary>The "open in a window" button (the keyboard / screen-reader way to tear out).</summary>
    public event EventHandler? PopOutRequested;

    /// <summary>True while hosted by the floating window.</summary>
    public bool IsFloating
    {
        get => _isFloating;
        set
        {
            _isFloating = value;
            UpdateHeaderHint();
        }
    }

    private bool _isFloating;

    public void FocusComposer()
    {
        Composer.Focus();
        Keyboard.Focus(Composer);
    }

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

    // ---- header drag: tear out of the page, or move the floating window ----------------------------------------------

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (IsFloating)
        {
            e.Handled = true;
            WindowDragRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _pressedAt = e.GetPosition(this);
        Header.CaptureMouse();
        e.Handled = true;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || !Header.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPress();
            return;
        }
        if ((e.GetPosition(this) - start).Length < TearOffDistance) return;
        EndPress();
        TearOffRequested?.Invoke(this, start);
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e) => EndPress();

    private void EndPress()
    {
        _pressedAt = null;
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
    }

    private bool IsInsideButton(DependencyObject? element)
    {
        for (var d = element; d is not null && !ReferenceEquals(d, Header); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase) return true;
        }
        return false;
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateHeaderHint()
    {
        Header.ToolTip = IsFloating ? "Drag to move the window" : "Drag out to open the chat in its own window";
        PopOutButton.Visibility = IsFloating ? Visibility.Collapsed : Visibility.Visible;
    }
}
