using Avalonia.Controls;
using Avalonia.Input;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The savings-goal sheets of the Wallet pop-up (<see cref="WalletPopupView"/>): a goal, its form, taking money out, the purchase.</summary>
public partial class GoalSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public GoalSheet() => InitializeComponent();

    public GoalSheet(GoalsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Shows the part for <paramref name="popup"/>; false when it is not one of the goal sheets.</summary>
    public bool Show(WalletPopup popup)
    {
        GoalSection.IsVisible = popup == WalletPopup.Goal;
        FormSection.IsVisible = popup == WalletPopup.GoalForm;
        TakeOutSection.IsVisible = popup == WalletPopup.GoalTakeOut;
        BoughtSection.IsVisible = popup == WalletPopup.GoalBought;
        return popup is WalletPopup.Goal or WalletPopup.GoalForm or WalletPopup.GoalTakeOut or WalletPopup.GoalBought;
    }

    /// <summary>The first box of the form shown, for the keyboard.</summary>
    public IInputElement? FirstBox(WalletPopup popup) => popup switch
    {
        WalletPopup.GoalForm when (DataContext as GoalsViewModel)?.FormIsNew == true => GoalNameBox,
        WalletPopup.GoalTakeOut => TakeAmountBox,
        _ => null,
    };

    /// <summary>Price and date side by side, or one above the other on a phone.</summary>
    public void SetNarrow(bool narrow)
    {
        var date = PriceRow.Children[1];
        Grid.SetColumn(date, narrow ? 0 : 1);
        Grid.SetColumnSpan(PriceRow.Children[0], narrow ? 2 : 1);
        Grid.SetColumnSpan(date, narrow ? 2 : 1);
        Grid.SetRow(date, narrow ? 1 : 0);
        PriceRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        PriceRow.RowSpacing = narrow ? 16 : 0;
    }
}
