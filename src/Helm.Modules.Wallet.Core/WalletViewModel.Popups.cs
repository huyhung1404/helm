using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Wallet;

/// <summary>What the Wallet pop-up shows (a dialog on the PC, a bottom sheet on the phone).</summary>
public enum WalletPopup
{
    None,
    Add,
    Details,
    Edit,

    /// <summary>A transaction's money into the debt book: lent, borrowed, paid back.</summary>
    Debt,

    ToCategorize,
    Fund,
    FundForm,

    /// <summary>Money moved into a fund from another for the month ("Cover it…").</summary>
    Cover,

    /// <summary>Every chart of the month (the phone; the PC shows them on the page).</summary>
    Charts,

    AddDebt,
    Person,

    /// <summary>A savings goal: its photo, setting money aside, its history.</summary>
    Goal,

    GoalForm,
    GoalTakeOut,

    /// <summary>The goal was bought: what it cost and its category.</summary>
    GoalBought,
}

/// <summary>
/// The Wallet pop-up: one at a time, and the one it was opened from comes back when it closes (a transaction opened
/// from "To categorize" returns there; its editor returns to it).
/// </summary>
public sealed partial class WalletViewModel
{
    private readonly Stack<WalletPopup> _popupBack = new();

    [ObservableProperty] private WalletPopup _popup;

    /// <summary>The transaction the details, editor and debt form show.</summary>
    [ObservableProperty] private TransactionRow? _selected;

    public bool HasPopup => Popup != WalletPopup.None;

    public bool ShowAddPopup => Popup == WalletPopup.Add;

    public bool ShowDetailsPopup => Popup == WalletPopup.Details;

    public bool ShowEditPopup => Popup == WalletPopup.Edit;

    public bool ShowDebtPopup => Popup == WalletPopup.Debt;

    public bool ShowToCategorizePopup => Popup == WalletPopup.ToCategorize;

    public bool ShowFundPopup => Popup == WalletPopup.Fund;

    public bool ShowFundFormPopup => Popup == WalletPopup.FundForm;

    public bool ShowCoverPopup => Popup == WalletPopup.Cover;

    public bool ShowChartsPopup => Popup == WalletPopup.Charts;

    public bool ShowAddDebtPopup => Popup == WalletPopup.AddDebt;

    public bool ShowPersonPopup => Popup == WalletPopup.Person;

    public bool ShowGoalPopup => Popup == WalletPopup.Goal;

    public bool ShowGoalFormPopup => Popup == WalletPopup.GoalForm;

    public bool ShowGoalTakeOutPopup => Popup == WalletPopup.GoalTakeOut;

    public bool ShowGoalBoughtPopup => Popup == WalletPopup.GoalBought;

    /// <summary>The add and edit forms share one layout; this is its title.</summary>
    public string FormTitle => Popup == WalletPopup.Edit ? "Edit transaction" : "Add transaction";

    partial void OnPopupChanged(WalletPopup value)
    {
        foreach (var name in (string[])[nameof(HasPopup), nameof(ShowAddPopup), nameof(ShowDetailsPopup), nameof(ShowEditPopup), nameof(ShowDebtPopup),
            nameof(ShowToCategorizePopup), nameof(ShowFundPopup), nameof(ShowFundFormPopup), nameof(ShowCoverPopup), nameof(ShowChartsPopup),
            nameof(ShowAddDebtPopup), nameof(ShowPersonPopup), nameof(ShowGoalPopup), nameof(ShowGoalFormPopup), nameof(ShowGoalTakeOutPopup),
            nameof(ShowGoalBoughtPopup), nameof(FormTitle)])
            OnPropertyChanged(name);
        if (value == WalletPopup.None) _popupBack.Clear();
    }

    /// <summary>Opens a pop-up; with <paramref name="stack"/>, the one shown now comes back when it closes.</summary>
    internal void ShowPopup(WalletPopup popup, bool stack = true)
    {
        if (stack && Popup != WalletPopup.None && Popup != popup) _popupBack.Push(Popup);
        else if (!stack) _popupBack.Clear();
        PopupError = null;
        Popup = popup;
    }

    /// <summary>Closes the pop-up shown: back to the one it was opened from, or none. Escape, the scrim, Cancel and ✕ do this.</summary>
    [RelayCommand]
    private void DismissPopup()
    {
        if (Popup == WalletPopup.Edit && Selected is { } row) row.IsEditing = false;
        PopupError = null;
        Popup = _popupBack.Count > 0 ? _popupBack.Pop() : WalletPopup.None;
    }

    /// <summary>Closes every pop-up ("Done").</summary>
    [RelayCommand]
    private void ClosePopup()
    {
        if (Selected is { } row) row.IsEditing = false;
        PopupError = null;
        Popup = WalletPopup.None;
    }

    /// <summary>A problem with what the pop-up's form holds, shown in the pop-up; null when there is none.</summary>
    [ObservableProperty] private string? _popupError;

    public bool HasPopupError => !string.IsNullOrEmpty(PopupError);

    partial void OnPopupErrorChanged(string? value) => OnPropertyChanged(nameof(HasPopupError));

    // ---- Transactions --------------------------------------------------------------------------------------------

    internal void OpenDetails(TransactionRow row)
    {
        Selected = row;
        ShowPopup(WalletPopup.Details, stack: Popup is WalletPopup.ToCategorize or WalletPopup.Fund);
    }

    internal void OpenEdit(TransactionRow row)
    {
        Selected = row;
        ShowPopup(WalletPopup.Edit);
    }

    internal void OpenDebtForm(TransactionRow row)
    {
        Selected = row;
        ShowPopup(WalletPopup.Debt);
    }

    /// <summary>The "To categorize (n)" button: on the money view while some wait for a category.</summary>
    public bool ShowToCategorizeButton => IsMoneyView && HasToCategorize;

    [RelayCommand]
    private void OpenToCategorize()
    {
        ShowPopup(WalletPopup.ToCategorize, stack: false);
        ProposeCategories();
    }

    [RelayCommand]
    private void OpenCharts() => ShowPopup(WalletPopup.Charts, stack: false);

    // ---- Funds ---------------------------------------------------------------------------------------------------

    /// <summary>The funds (jars) of the month; null where the app has none (tests).</summary>
    public FundsViewModel? Funds { get; }

    /// <summary>The old single monthly budget shows only while there are no funds (the funds are the budget then).</summary>
    public bool ShowBudgetCard => HasBudget && Funds is not { HasFunds: true };

    internal void OpenFund(FundRow row)
    {
        if (Funds is null) return;
        Funds.Selected = row;
        RefreshFundRows(_store.Transactions(), Categories());
        ShowPopup(WalletPopup.Fund, stack: false);
    }

    /// <summary>The selected fund's spending this month, for its pop-up.</summary>
    private void RefreshFundRows(IReadOnlyList<Helm.Core.Sync.SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories)
    {
        if (Funds?.Selected is not { } fund)
        {
            Funds?.ShowRows([]);
            return;
        }
        var ids = fund.State.Fund.Categories.ToHashSet(StringComparer.Ordinal);
        var rows = transactions
            .Where(t => t.Value.Amount < 0 && t.Value.CategoryId is { } c && ids.Contains(c))
            .Where(t => WalletFormat.LocalDay(t.Value.OccurredAt, _zone) is var d && d.Year == _month.Year && d.Month == _month.Month)
            .Select(t => RowFor(t.Id, t.Value, categories))
            .ToList();
        Funds.ShowRows(rows);
    }

    /// <summary>The selected transaction was deleted: its pop-ups go, back to where it was opened from.</summary>
    private void SelectedGone()
    {
        while (Popup is WalletPopup.Details or WalletPopup.Edit or WalletPopup.Debt) DismissPopup();
        Selected = null;
    }
}
