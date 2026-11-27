using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Wallet;

// The quick add on the Money page ("45k cà phê", Enter), as Quick Capture's "/s" does; its options open the full form
// with what was typed. And what the shared page asks of the view model besides.
public sealed partial class WalletViewModel
{
    /// <summary>No transaction at all yet (any month): the page shows the way in instead of empty numbers.</summary>
    [ObservableProperty] private bool _isFirstRun;

    /// <summary>What is typed in the quick add: the amount first ("+" for money in), then what it was for.</summary>
    [ObservableProperty] private string _quickText = "";

    /// <summary>What Enter would save ("Spending 45,000 ₫ · cà phê · Food & drinks"), or how to type it.</summary>
    public string QuickHint
    {
        get
        {
            if (QuickText.Trim().Length == 0) return "";
            if (!SpendCaptureTarget.TryRead(QuickText, out var amount, out var description)) return "Start with the amount, e.g. 45k cà phê, or +2tr for money in.";
            var category = _store.SureCategory(description, amount) is { } id ? _store.Category(id)?.Name : null;
            var parts = new List<string> { $"{(amount < 0 ? "Spending" : "Money in")} {WalletFormat.Money(Math.Abs(amount))}" };
            if (description.Length > 0) parts.Add(description);
            parts.Add(category ?? "to categorize");
            return string.Join(" · ", parts);
        }
    }

    public bool HasQuickHint => QuickHint.Length > 0;

    /// <summary>The quick add reads as an amount: Enter saves it.</summary>
    public bool QuickReads => SpendCaptureTarget.TryRead(QuickText, out _, out _);

    partial void OnQuickTextChanged(string value)
    {
        OnPropertyChanged(nameof(QuickHint));
        OnPropertyChanged(nameof(HasQuickHint));
        OnPropertyChanged(nameof(QuickReads));
    }

    /// <summary>Saves what the quick add says, now, in the category its description always had (or to categorize).</summary>
    [RelayCommand]
    private void QuickAdd()
    {
        if (!SpendCaptureTarget.TryRead(QuickText, out var amount, out var description))
        {
            // Nothing that reads as an amount: the full form, with what was typed as its description.
            OpenAddWith(QuickText.Trim(), "");
            return;
        }
        try
        {
            var category = _store.SureCategory(description, amount);
            _store.AddManual(amount, description, _store.Now, category);
            QuickText = "";
            Message = null;
            // Added now: the current month shows it.
            _month = WalletFormat.MonthOf(_store.Now, _zone);
            Refresh();
            ShowToast($"Added {WalletFormat.Money(Math.Abs(amount))}" + (category is { } c ? FundNote(c) : ""));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Report(ex, "Could not save the transaction.");
        }
    }

    /// <summary>The quick add's options: the full form (kind, category, day, note) with what was typed in it.</summary>
    [RelayCommand]
    private void OpenQuickOptions()
    {
        if (SpendCaptureTarget.TryRead(QuickText, out var amount, out var description))
            OpenAddWith(description, Math.Abs(amount).ToString("#,0", System.Globalization.CultureInfo.CurrentCulture), income: amount > 0);
        else
            OpenAddWith(QuickText.Trim(), "");
    }

    private void OpenAddWith(string description, string amount, bool income = false)
    {
        OpenAddCommand.Execute(null);
        NewDescription = description;
        NewAmount = amount;
        if (income) NewKindIndex = 1;
        QuickText = "";
    }

    /// <summary>The confirmations (delete a transaction, a fund, a goal…) open in the page's own dialog.</summary>
    public void UseDialogs(Helm.Shell.Services.IDialogService dialogs)
    {
        _dialogs = dialogs;
        Funds?.UseDialogs(dialogs);
        Goals?.UseDialogs(dialogs);
        _debts?.UseDialogs(dialogs);
    }

    /// <summary>This device has the home-screen widget (the phone): its settings show.</summary>
    public bool HasWidget => _platform is not null;
}
