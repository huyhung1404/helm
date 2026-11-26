using Avalonia.Controls;
using Avalonia.Input;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The fund sheets of the Wallet pop-up (<see cref="WalletPopupView"/>): a fund, its form, moving money into it.</summary>
public partial class FundSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public FundSheet() => InitializeComponent();

    public FundSheet(FundsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Shows the part for <paramref name="popup"/>; false when it is not one of the fund sheets.</summary>
    public bool Show(WalletPopup popup)
    {
        FundSection.IsVisible = popup == WalletPopup.Fund;
        FormSection.IsVisible = popup == WalletPopup.FundForm;
        CoverSection.IsVisible = popup == WalletPopup.Cover;
        return popup is WalletPopup.Fund or WalletPopup.FundForm or WalletPopup.Cover;
    }

    /// <summary>The first box of the form shown, for the keyboard (a new fund: its name).</summary>
    public IInputElement? FirstBox(WalletPopup popup) => popup switch
    {
        WalletPopup.FundForm when (DataContext as FundsViewModel)?.FormId is null => FundNameBox,
        WalletPopup.Cover => CoverAmountBox,
        _ => null,
    };
}
