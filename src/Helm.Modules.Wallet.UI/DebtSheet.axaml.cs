using Avalonia.Controls;
using Avalonia.Input;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The debt-book sheets of the Wallet pop-up (<see cref="WalletPopupView"/>): a new debt, and a person.</summary>
public partial class DebtSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public DebtSheet() => InitializeComponent();

    public DebtSheet(DebtsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Shows the part for <paramref name="popup"/>; false when it is not one of the debt sheets.</summary>
    public bool Show(WalletPopup popup)
    {
        AddSection.IsVisible = popup == WalletPopup.AddDebt;
        PersonSection.IsVisible = popup == WalletPopup.Person;
        return popup is WalletPopup.AddDebt or WalletPopup.Person;
    }

    /// <summary>The name box of a new debt that has no person yet, for the keyboard.</summary>
    public IInputElement? FirstBox(WalletPopup popup) =>
        popup == WalletPopup.AddDebt && (DataContext as DebtsViewModel)?.NewPerson.Length == 0 ? PersonBox : null;
}
