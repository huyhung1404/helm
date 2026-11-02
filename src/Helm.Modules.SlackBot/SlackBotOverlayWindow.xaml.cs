using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Helm.Core.Desktop;

namespace Helm.Modules.SlackBot;

/// <summary>
/// SlackBot's list on top of every app, in the bottom-right corner or wherever the user dragged it (by any spot that is
/// not a button; a double-click puts it back). It never takes the keyboard from the app in front. Folded, it is a badge
/// with the count; a click opens it again.
/// </summary>
public partial class SlackBotOverlayWindow : Window
{
    private readonly Action<Point?> _moved;
    private readonly Action<bool> _folded;
    private readonly SlackBotInboxViewModel _viewModel;
    private readonly IWindowService _windows;
    private Point? _anchor;
    private double _rest = 1;
    private nint _handle;
    private nint _previous;
    private bool _typing;

    /// <param name="anchor">Where the user put it last: its bottom-left corner; null for the bottom-right corner.</param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    /// <param name="folded">Keeps whether it is folded.</param>
    public SlackBotOverlayWindow(SlackBotInboxViewModel viewModel, IWindowService windows, Point? anchor, bool fold, Action<Point?> moved, Action<bool> folded)
    {
        _anchor = anchor;
        _moved = moved;
        _folded = folded;
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            windows.MakeNonActivating(_handle);
        };
        // Reply on a message: the box opens and takes the keyboard; closed (sent or cancelled): the keyboard goes back.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SlackBotInboxViewModel.ReplyTo)) return;
            if (viewModel.ReplyTo is null) StopTyping();
            else
            {
                if (IsFolded) SetFolded(false);
                StartTyping();
            }
        };
        Closed += (_, _) => StopTyping();
        // It grows and shrinks from its bottom edge, as items come and go.
        SizeChanged += (_, _) => Place();
        // See-through while the mouse is elsewhere, solid under it.
        MouseEnter += (_, _) => Opacity = 1;
        MouseLeave += (_, _) => Opacity = _rest;
        SetFolded(fold);
    }

    /// <summary>Solid for a few seconds, to be seen (a reminder).</summary>
    public void Flash()
    {
        Opacity = 1;
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            if (!IsMouseOver) Opacity = _rest;
        };
        back.Start();
    }

    /// <summary>How opaque it is while the mouse is not on it.</summary>
    public double RestOpacity
    {
        get => _rest;
        set
        {
            _rest = Math.Clamp(value, 0.3, 1);
            if (!IsMouseOver) Opacity = _rest;
        }
    }

    public bool IsFolded => Folded.Visibility == Visibility.Visible;

    public void ShowOverlay()
    {
        if (IsVisible) return;
        // Off-screen first, so it never flashes at the wrong spot before its size is known.
        Left = -32000;
        Top = -32000;
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    public void SetFolded(bool fold)
    {
        Folded.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
        Open.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Bottom-right of the primary screen's work area (above the taskbar), or the spot the user dragged it to; a spot
    /// off every screen (a monitor unplugged) is pulled back in.
    /// </summary>
    private void Place()
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        if (_anchor is not { } at)
        {
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            return;
        }
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(at.X, screens.Left, Math.Max(screens.Left, screens.Right - ActualWidth));
        Top = Math.Clamp(at.Y - ActualHeight, screens.Top, Math.Max(screens.Top, screens.Bottom - ActualHeight));
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _anchor = null;
            _moved(null);
            Place();
            return;
        }
        var (left, top) = (Left, Top);
        DragMove();
        if (Math.Abs(Left - left) < 2 && Math.Abs(Top - top) < 2)
        {
            // A click, not a drag: on the badge it opens the list.
            if (IsFolded)
            {
                SetFolded(false);
                _folded(false);
            }
            return;
        }
        _anchor = new Point(Left, Top + ActualHeight);
        _moved(_anchor);
    }

    /// <summary>The overlay takes the keyboard from the app in front, which gets it back when the reply is done.</summary>
    private void StartTyping()
    {
        if (_handle == 0) return;
        if (!_typing)
        {
            _typing = true;
            _previous = _windows.GetForegroundWindow();
            _windows.AllowActivation(_handle, true);
        }
        Opacity = 1;
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ReplyBox.Focus();
            Keyboard.Focus(ReplyBox);
            ReplyBox.CaretIndex = ReplyBox.Text.Length;
        });
    }

    private void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        if (_handle != 0) _windows.AllowActivation(_handle, false);
        if (_previous != 0 && _previous != _handle) _windows.Activate(_previous);
        _previous = 0;
        if (!IsMouseOver) Opacity = _rest;
    }

    private void ReplyBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_typing) StartTyping();
    }

    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelReplyCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _viewModel.SendReplyCommand.Execute(null);
        }
    }

    private void SendReply_Click(object sender, RoutedEventArgs e) => _viewModel.SendReplyCommand.Execute(null);

    private void CancelReply_Click(object sender, RoutedEventArgs e) => _viewModel.CancelReplyCommand.Execute(null);

    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        SetFolded(true);
        _folded(true);
    }
}
