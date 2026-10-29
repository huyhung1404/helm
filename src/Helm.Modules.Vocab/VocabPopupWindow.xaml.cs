using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Helm.Core.Desktop;

namespace Helm.Modules.Vocab;

/// <summary>
/// The pop-up card in the bottom-right corner. It never takes the keyboard focus from the app in front. Unanswered and
/// untouched, it goes away by itself after <see cref="Linger"/>; once the meaning is shown it stays until answered.
/// </summary>
public partial class VocabPopupWindow : Window
{
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    private readonly VocabCardViewModel _card;
    private readonly DispatcherTimer _linger = new() { Interval = Linger };
    private readonly Action<string>? _openWord;

    /// <param name="openWord">Opens a word in Vocabulary with its whole tree ("See the whole tree").</param>
    public VocabPopupWindow(VocabCardViewModel card, IWindowService windows, Action<string>? openWord = null)
    {
        _card = card;
        _openWord = openWord;
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

    /// <summary>Bottom-right of the primary screen's work area (above the taskbar), growing upwards.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SeeTree_Click(object sender, RoutedEventArgs e)
    {
        if (_card.WordId is { } id) _openWord?.Invoke(id);
        Close();
    }
}
