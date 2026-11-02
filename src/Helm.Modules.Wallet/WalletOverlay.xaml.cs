using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet pop-up (see <see cref="WalletViewModel.Popup"/>) and its passing notices, shown by
/// <see cref="WalletContentPage"/> over the area the pages scroll in, so it stays put however far the page is scrolled
/// and leaves the menu usable. Escape closes a dialog (back to the one it was opened from); a click beside a dialog
/// with no form (details, a fund, a person, the list to categorize) closes it too.
/// </summary>
public partial class WalletOverlay : UserControl
{
    private readonly WalletViewModel _viewModel;
    private readonly ConditionalWeakTable<TextBox, string> _amountTexts = [];
    private bool _grouping;

    public WalletOverlay(WalletViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Focusable, so Escape reaches it even while nothing inside has the keyboard.
        Focusable = true;
        FocusVisualStyle = null;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_viewModel.HasPopup) return;
            _viewModel.DismissPopupCommand.Execute(null);
            e.Handled = true;
        };
        // The card is never taller than the area it is shown over.
        Scrim.SizeChanged += (_, _) => Card.MaxHeight = Math.Max(200, Scrim.ActualHeight - 48);
    }

    /// <summary>Sizes the card for what it shows and puts the keyboard in its first box.</summary>
    public void Place()
    {
        Card.Width = _viewModel.Popup switch
        {
            WalletPopup.ToCategorize => 680,
            WalletPopup.Fund or WalletPopup.FundForm or WalletPopup.Person => 620,
            WalletPopup.Debt or WalletPopup.Cover => 520,
            _ => 580,
        };
        IsHitTestVisible = _viewModel.HasPopup;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, FocusContent);
    }

    private void FocusContent()
    {
        IInputElement? target = _viewModel.Popup switch
        {
            WalletPopup.Add => NewAmountBox,
            WalletPopup.Edit => EditAmountBox,
            WalletPopup.FundForm => FundNameBox,
            WalletPopup.AddDebt => PersonBox,
            _ => null,
        };
        if (target is UIElement { IsVisible: true } element) element.Focus();
        else Focus();
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Forms keep what was typed; only a dialog without one closes from beside it.
        if (_viewModel.Popup is WalletPopup.Details or WalletPopup.Fund or WalletPopup.Person or WalletPopup.ToCategorize)
            _viewModel.DismissPopupCommand.Execute(null);
    }

    // A click on the card itself must not reach the scrim.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Thousands separators while an amount is typed, so 1000000 reads 1.000.000; the caret stays put.</summary>
    private void OnAmountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_grouping || sender is not TextBox box) return;
        var previous = _amountTexts.TryGetValue(box, out var before) ? before : "";
        var (text, caret) = WalletFormat.GroupDigits(box.Text, box.CaretIndex, previous);
        _amountTexts.AddOrUpdate(box, text);
        if (text == box.Text) return;
        _grouping = true;
        try
        {
            box.Text = text;
            box.CaretIndex = caret;
        }
        finally
        {
            _grouping = false;
        }
    }
}

/// <summary>Shows the <see cref="WalletOverlay"/> over another element (the pages' scroll area), at its size.</summary>
internal sealed class WalletOverlayAdorner : Adorner
{
    private readonly WalletOverlay _popup;

    public WalletOverlayAdorner(UIElement adorned, WalletOverlay popup)
        : base(adorned)
    {
        _popup = popup;
        AddVisualChild(popup);
    }

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
