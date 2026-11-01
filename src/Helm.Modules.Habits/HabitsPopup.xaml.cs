using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.Habits;

/// <summary>
/// The Habits pop-up (see <see cref="HabitsViewModel.HasPopup"/>), shown by <see cref="HabitsContentPage"/> over the
/// area the pages scroll in, so it stays put however far the page is scrolled and leaves the menu usable. A click on the
/// scrim around the card, or Escape, closes it when the view model allows.
/// </summary>
public partial class HabitsPopup : UserControl
{
    private readonly HabitsViewModel _viewModel;

    public HabitsPopup(HabitsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Focusable, so Escape reaches it even while nothing inside has the keyboard (the celebration).
        Focusable = true;
        FocusVisualStyle = null;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _viewModel.DismissPopupCommand.Execute(null);
            e.Handled = true;
        };
    }

    public void PlayConfetti(bool big) => Confetti.Play(big);

    /// <summary>Puts the keyboard where the pop-up wants it: the reason box, or the slider of a day being marked.</summary>
    public void FocusContent()
    {
        if (_viewModel.ShowLapse) ReasonBox.Focus();
        else if (_viewModel.Mark is { AsksPercent: true }) PercentSlider.Focus();
        else Focus();
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _viewModel.DismissPopupCommand.Execute(null);

    // A click on the card itself must not reach the scrim.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}

/// <summary>Shows the <see cref="HabitsPopup"/> over another element (the pages' scroll area), at its size.</summary>
internal sealed class HabitsPopupAdorner : Adorner
{
    private readonly HabitsPopup _popup;

    public HabitsPopupAdorner(UIElement adorned, HabitsPopup popup)
        : base(adorned)
    {
        _popup = popup;
        AddVisualChild(popup);
    }

    public HabitsPopup Popup => _popup;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _popup;

    protected override Size MeasureOverride(Size constraint)
    {
        _popup.Measure(AdornedElement.RenderSize);
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _popup.Arrange(new Rect(AdornedElement.RenderSize));
        return finalSize;
    }
}
