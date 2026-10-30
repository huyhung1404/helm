using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Helm.Core.Desktop;

namespace Helm.Modules.Vocab;

/// <summary>
/// The pop-up card in the bottom-right corner, or wherever the user dragged it (by any spot that is not a button;
/// a double-click puts it back in the corner). It never takes the keyboard focus from the app in front. Unanswered and
/// untouched, it goes away by itself after <see cref="Linger"/>; once the meaning is shown it stays until answered.
/// </summary>
public partial class VocabPopupWindow : Window
{
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    private readonly VocabCardViewModel _card;
    private readonly DispatcherTimer _linger = new() { Interval = Linger };
    private readonly Action<string>? _openWord;
    private readonly Action<Point?>? _moved;
    private Point? _anchor;

    /// <param name="openWord">Opens a word in Vocabulary with its whole tree ("See the whole tree").</param>
    /// <param name="anchor">Where the user put it last: its bottom-left corner; null for the bottom-right corner.</param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    public VocabPopupWindow(VocabCardViewModel card, IWindowService windows, Action<string>? openWord = null, Point? anchor = null, Action<Point?>? moved = null)
    {
        _card = card;
        _openWord = openWord;
        _anchor = anchor;
        _moved = moved;
        DataContext = card;
        InitializeComponent();
        SourceInitialized += (_, _) => windows.MakeNonActivating(new WindowInteropHelper(this).Handle);
        card.Answered += (_, _) => Close();
        _linger.Tick += (_, _) =>
        {
            if (IsMouseOver || _card.IsRevealed) return;
            Close();
        };
        Closed += (_, _) => _linger.Stop();
    }

    public void ShowCard()
    {
        // Off-screen first, so it never flashes at the wrong spot before its height is known.
        Left = -32000;
        Top = -32000;
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        _card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VocabCardViewModel.IsRevealed)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        };
        _linger.Start();
    }

    /// <summary>
    /// Bottom-right of the primary screen's work area (above the taskbar), or the spot the user dragged it to; growing
    /// upwards either way. A spot off every screen (a monitor unplugged) is pulled back in.
    /// </summary>
    private void Place()
    {
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

    /// <summary>Dragging the card (not its buttons) moves it; a double-click puts it back in the corner.</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _anchor = null;
            _moved?.Invoke(null);
            Place();
            return;
        }
        DragMove();
        _anchor = new Point(Left, Top + ActualHeight);
        _moved?.Invoke(_anchor);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SeeTree_Click(object sender, RoutedEventArgs e)
    {
        if (_card.WordId is { } id) _openWord?.Invoke(id);
        Close();
    }
}
