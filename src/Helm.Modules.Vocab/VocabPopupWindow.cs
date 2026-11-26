using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Modules.Vocab.Ui;
using Helm.Ui.Windows;

namespace Helm.Modules.Vocab;

/// <summary>
/// The pop-up card on Windows: the shared <see cref="VocabPopupCard"/> in a see-through Avalonia window of its own
/// (<see cref="AvaloniaCornerWindow"/>) in the bottom-right corner, or wherever the user dragged it (by any spot that is
/// not a button; a double-click puts it back in the corner). It never takes the keyboard focus from the app in front,
/// except while its pinyin box is used: then it takes the keyboard and gives it back to that app once checked.
/// Unanswered and untouched, it goes away by itself after <see cref="Linger"/>; being typed in or read, it stays until
/// answered. "Full details" grows it to the screen's height, scrolling inside.
/// </summary>
public sealed class VocabPopupWindow : IVocabPopupHost
{
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    /// <summary>The card's width; the window adds room for its shadow all round.</summary>
    private const double CardWidth = 320;

    /// <summary>Room the card's other rows take (question, word, verdict, icons) when Full details fills the screen.</summary>
    private const double CardChrome = 300;

    private readonly VocabCardViewModel _card;
    private readonly Action<string>? _openWord;
    private readonly AvaloniaCornerWindow _window;
    private readonly VocabPopupCard _view;
    private readonly DispatcherTimer _linger = new() { Interval = Linger };

    /// <param name="openWord">Opens a word in Vocabulary with its whole tree ("Open in Helm").</param>
    /// <param name="anchor">Where the user put it last: its bottom-left corner; null for the bottom-right corner.</param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    /// <param name="opacity">How solid the card is at rest, 0-1 (the settings' Card opacity).</param>
    public VocabPopupWindow(VocabCardViewModel card, IWindowService windows, IMonitorService monitors, Action<string>? openWord = null, Point? anchor = null,
        Action<Point?>? moved = null, double opacity = 1)
    {
        _card = card;
        _openWord = openWord;
        _window = new AvaloniaCornerWindow(windows, monitors, "Vocabulary", CardWidth + 2 * AvaloniaCornerWindow.ShadowSpace, anchor, at => moved?.Invoke(at));
        _view = new VocabPopupCard(card, this, opacity) { Margin = new Thickness(AvaloniaCornerWindow.ShadowSpace) };
        _window.Content = _view;
        card.Answered += OnAnswered;
        _linger.Tick += (_, _) =>
        {
            if (_view.CanGoAway) Close();
        };
        _window.Closed += (_, _) =>
        {
            _linger.Stop();
            card.Answered -= OnAnswered;
            _view.StopTyping();
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised once the card is gone, however it was closed.</summary>
    public event EventHandler? Closed;

    public bool IsVisible => _window.IsVisible;

    public void ShowCard()
    {
        _window.Show();
        _linger.Start();
    }

    /// <summary>How solid the card is at rest (the setting changed while it is shown).</summary>
    public void SetRestOpacity(double opacity) => _view.SetRestOpacity(opacity);

    public void Close() => _window.Close();

    public void OpenWord(string id) => _openWord?.Invoke(id);

    public void TakeKeyboard() => _window.TakeKeyboard();

    public void ReturnKeyboard() => _window.ReturnKeyboard();

    public void Drag(PointerPressedEventArgs e) => _window.Drag(e);

    public void BackToCorner() => _window.BackToCorner();

    public double FullHeight => _window.WorkAreaHeight - CardChrome;

    private void OnAnswered(object? sender, EventArgs e) => Close();
}
