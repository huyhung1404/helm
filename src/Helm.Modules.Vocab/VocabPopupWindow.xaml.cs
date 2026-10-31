using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Helm.Core.Desktop;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vocab;

/// <summary>
/// The pop-up card in the bottom-right corner, or wherever the user dragged it (by any spot that is not a button;
/// a double-click puts it back in the corner). It never takes the keyboard focus from the app in front, except while
/// its pinyin box is used: then it takes the keyboard and gives it back to that app once checked. Unanswered and
/// untouched, it goes away by itself after <see cref="Linger"/>; being typed in or read, it stays until answered.
/// "Full details" grows it to the screen's height with everything known about the word, scrolling inside.
/// </summary>
public partial class VocabPopupWindow : Window
{
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    /// <summary>How tall the card grows before it scrolls, unless Full details is open.</summary>
    private const double CompactHeight = 460;

    private readonly VocabCardViewModel _card;
    private readonly IWindowService _windows;
    private readonly DispatcherTimer _linger = new() { Interval = Linger };
    private readonly Action<string>? _openWord;
    private readonly Action<Point?>? _moved;
    private Point? _anchor;
    private nint _handle;
    private nint _previous;
    private bool _typing, _full;

    /// <param name="openWord">Opens a word in Vocabulary with its whole tree ("Open in Helm").</param>
    /// <param name="anchor">Where the user put it last: its bottom-left corner; null for the bottom-right corner.</param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    public VocabPopupWindow(VocabCardViewModel card, IWindowService windows, Action<string>? openWord = null, Point? anchor = null, Action<Point?>? moved = null)
    {
        _card = card;
        _windows = windows;
        _openWord = openWord;
        _anchor = anchor;
        _moved = moved;
        DataContext = card;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            windows.MakeNonActivating(_handle);
        };
        card.Answered += (_, _) => Close();
        card.PropertyChanged += OnCardChanged;
        _linger.Tick += (_, _) =>
        {
            if (IsMouseOver || _typing || _card.IsRevealed || _card.Typed.Length > 0) return;
            Close();
        };
        Closed += (_, _) =>
        {
            _linger.Stop();
            card.PropertyChanged -= OnCardChanged;
            StopTyping();
        };
        Update();
    }

    public void ShowCard()
    {
        // Off-screen first, so it never flashes at the wrong spot before its height is known.
        Left = -32000;
        Top = -32000;
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        // It grows upwards: the answer, Full details.
        SizeChanged += (_, _) => Place();
        _linger.Start();
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabCardViewModel.IsRevealed) or nameof(VocabCardViewModel.IsTyping)
            or nameof(VocabCardViewModel.SuggestsRemembered) or nameof(VocabCardViewModel.SuggestsForgot))
            Update();
    }

    /// <summary>What shows: the hint or the pinyin box, the short parts or Full details, the suggested verdict.</summary>
    private void Update()
    {
        var typing = _card.IsTyping;
        TapHint.Visibility = !typing && !_card.IsRevealed ? Visibility.Visible : Visibility.Collapsed;
        TypePanel.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        PartsBox.Visibility = !_full && _card.HasParts ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = _full ? Visibility.Visible : Visibility.Collapsed;
        FullButton.Icon = new SymbolIcon(_full ? SymbolRegular.ArrowMinimize24 : SymbolRegular.ArrowMaximize24);
        FullButton.ToolTip = _full ? "Less" : "Full details";
        System.Windows.Automation.AutomationProperties.SetName(FullButton, _full ? "Less" : "Full details");
        Ring(RememberedButton, _card.SuggestsRemembered);
        Ring(ForgotButton, _card.SuggestsForgot);
    }

    /// <summary>The verdict the typed answer suggests gets a ring; the user still chooses.</summary>
    private void Ring(Wpf.Ui.Controls.Button button, bool on)
    {
        button.BorderThickness = new Thickness(on ? 2.5 : 0);
        button.BorderBrush = on ? TryFindResource("AccentTextFillColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue : null;
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

    /// <summary>Dragging the card (not its buttons or box) moves it; a double-click puts it back in the corner.</summary>
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

    private void Word_Click(object sender, RoutedEventArgs e)
    {
        if (_card.IsTyping)
        {
            StartTyping();
            return;
        }
        Reveal();
    }

    private void TypeBox_MouseDown(object sender, MouseButtonEventArgs e) => StartTyping();

    private void TypeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Check();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            StopTyping();
            e.Handled = true;
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => Check();

    private void ShowMeaning_Click(object sender, RoutedEventArgs e) => Reveal();

    private void Check()
    {
        if (_card.Typed.Trim().Length == 0)
        {
            StartTyping();
            return;
        }
        StopTyping();
        _card.CheckCommand.Execute(null);
    }

    private void Reveal()
    {
        StopTyping();
        if (!_card.IsRevealed) _card.RevealCommand.Execute(null);
    }

    /// <summary>The box is used: the card takes the keyboard from the app in front, which gets it back afterwards.</summary>
    private void StartTyping()
    {
        if (_typing || _handle == 0) return;
        _typing = true;
        _previous = _windows.GetForegroundWindow();
        _windows.AllowActivation(_handle, true);
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(TypeBox));
    }

    private void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        if (_handle != 0) _windows.AllowActivation(_handle, false);
        if (_previous != 0 && _previous != _handle) _windows.Activate(_previous);
        _previous = 0;
    }

    private void Full_Click(object sender, RoutedEventArgs e) => ToggleFull();

    /// <summary>Opens Full details, or closes it.</summary>
    internal void ToggleFull()
    {
        _full = !_full;
        if (_full) DetailsList.ItemsSource = _card.Details();
        Scroller.MaxHeight = _full ? Math.Max(CompactHeight, SystemParameters.WorkArea.Height - 72) : CompactHeight;
        Update();
        Scroller.ScrollToTop();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_card.WordId is { } id) _openWord?.Invoke(id);
        Close();
    }
}
