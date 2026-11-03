using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace Helm.Modules.NovelReader;

// The held sentence's sheet at the bottom covers the last lines of the view: the text scrolls up so the line that was
// held stays in sight above it, while the sheet grows with the explanation. At a chapter's end the list gets room
// under its last line for that, taken back when the sheet closes.
public partial class NovelReaderContentPage
{
    /// <summary>The list's room under its last paragraph when no sheet is open (its Margin in the XAML).</summary>
    private const double ListBottom = 48;

    /// <summary>Space kept between the held line and the sheet's top.</summary>
    private const double AboveSheet = 12;

    /// <summary>Where the held line ends, in the text's own coordinates (the scroll offset plus the point in view); null for none.</summary>
    private double? _heldBottom;

    private void HookHeldSentence()
    {
        HeldPanel.SizeChanged += (_, _) => KeepHeldInView();
        _viewModel.HeldSentence.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(HeldSentenceViewModel.IsShown) || _viewModel.HeldSentence.IsShown) return;
            _heldBottom = null;
            if (ParagraphList.Margin.Bottom != ListBottom) ParagraphList.Margin = new Thickness(0, 0, 16, ListBottom);
        };
    }

    /// <summary>The mouse held a word: the bottom of its line (half a line, at 1.5 times the text size, under the point held).</summary>
    private void RememberHeld(object? sender)
    {
        // The text's size where it was held (a word block in study mode, the paragraph's text otherwise).
        var line = (sender is DependencyObject held ? TextElement.GetFontSize(held) : 20) * 1.5;
        _heldBottom = Scroller.VerticalOffset + Mouse.GetPosition(Scroller).Y + line / 2;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, KeepHeldInView);
    }

    /// <summary>Scrolls just enough that the held line is above the sheet (nothing moves while it already is).</summary>
    private void KeepHeldInView()
    {
        if (_heldBottom is not { } bottom || !_viewModel.HeldSentence.IsShown || HeldPanel.ActualHeight <= 0) return;
        var sheet = HeldPanel.ActualHeight + HeldPanel.Margin.Bottom;
        var over = bottom - Scroller.VerticalOffset - (Scroller.ViewportHeight - sheet - AboveSheet);
        if (over <= 0) return;
        var target = Scroller.VerticalOffset + over;
        if (target > Scroller.ScrollableHeight && ParagraphList.Margin.Bottom < sheet + AboveSheet * 2)
        {
            // The chapter's last lines: room under them first, then scroll once it is laid out.
            ParagraphList.Margin = new Thickness(0, 0, 16, sheet + AboveSheet * 2);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SmoothScroll(target));
            return;
        }
        SmoothScroll(target);
    }
}
