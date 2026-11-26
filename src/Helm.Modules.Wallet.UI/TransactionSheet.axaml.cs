using Avalonia.Controls;
using Avalonia.Input;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// The transaction sheets of the Wallet pop-up (<see cref="WalletPopupView"/>): add, details, edit, the debt book, and
/// the list to categorize. Each shows while <see cref="WalletViewModel.Popup"/> says so.
/// </summary>
public partial class TransactionSheet : UserControl
{
    /// <summary>The add form's kinds; a move between my own accounts is "Transfer" (the line under it says what it means).</summary>
    public static IReadOnlyList<string> AddKinds { get; } = ["Spending", "Money in", "Transfer"];

    /// <summary>The editor's kinds (a transfer is a category, picked in the details).</summary>
    public static IReadOnlyList<string> EditKinds { get; } = ["Spending", "Money in"];

    /// <summary>For the XAML loader and the previewer only.</summary>
    public TransactionSheet() => InitializeComponent();

    public TransactionSheet(WalletViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The first box of the form shown, for the keyboard; null when the sheet has no form.</summary>
    public IInputElement? FirstBox(WalletPopup popup) => popup switch
    {
        WalletPopup.Add => NewAmountBox,
        WalletPopup.Debt => DebtPersonBox,
        _ => null,
    };
}
