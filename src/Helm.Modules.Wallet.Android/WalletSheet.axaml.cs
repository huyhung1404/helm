using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Wallet;

/// <summary>
/// Over the whole screen while the Wallet page is shown (see <see cref="WalletContentPage"/>): the floating buttons,
/// the passing notice and the bottom sheet of <see cref="WalletViewModel.Popup"/>. A tap on the scrim closes a sheet
/// that has no form (details, a fund, a person, the list to categorize, the charts); forms keep what was typed.
/// </summary>
public partial class WalletSheet : UserControl
{
    private readonly WalletViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the page creates it with its view model.</summary>
    public WalletSheet()
        : this(HelmAndroidServices.Current.GetRequiredService<WalletViewModel>())
    {
    }

    public WalletSheet(WalletViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // A tap on the sheet itself must not reach the scrim.
        Sheet.PointerPressed += (_, e) => e.Handled = true;
        Scrim.PointerPressed += (_, _) =>
        {
            if (_viewModel.Popup is WalletPopup.Details or WalletPopup.Fund or WalletPopup.Person or WalletPopup.ToCategorize or WalletPopup.Charts or WalletPopup.Goal)
                _viewModel.DismissPopupCommand.Execute(null);
        };
    }

    /// <summary>The editor's two kinds (a transfer is a category, picked in the details).</summary>
    public static IReadOnlyList<string> EditKinds { get; } = ["Spending", "Money in"];

    /// <summary>Sizes the sheet to the screen (never taller than most of it) and puts the keyboard in its first box.</summary>
    public void Place(double height)
    {
        Sheet.MaxHeight = Math.Max(240, height * 0.92);
        Dispatcher.UIThread.Post(() =>
        {
            Control? target = _viewModel.Popup switch
            {
                WalletPopup.Add => NewAmountBox,
                WalletPopup.FundForm when _viewModel.Funds?.FormId is null => FundNameBox,
                WalletPopup.AddDebt when _viewModel.Debts?.NewPerson.Length == 0 => PersonBox,
                WalletPopup.GoalForm when _viewModel.Goals?.FormIsNew == true => GoalNameBox,
                WalletPopup.GoalTakeOut => TakeAmountBox,
                _ => null,
            };
            target?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The floating buttons follow the tool's on/off state.</summary>
    public void SetEnabled(bool on)
    {
        AddButton.IsEnabled = on;
        AddDebtButton.IsEnabled = on;
        AddGoalButton.IsEnabled = on;
        ToCategorizeButton.IsEnabled = on;
    }
}
