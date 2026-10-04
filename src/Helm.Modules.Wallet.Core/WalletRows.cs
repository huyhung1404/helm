using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Wallet;

/// <summary>A category in a picker.</summary>
public sealed record CategoryChoice(string Id, string Name);

/// <summary>A one-tap category button under a transaction to categorize.</summary>
public sealed class CategoryChip(string id, string name, Action<string> pick)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public IRelayCommand PickCommand { get; } = new RelayCommand(() => pick(id));
}

/// <summary>A category's share of the month (the bars on the Wallet page).</summary>
public sealed record CategorySpendRow(string Name, string AmountText, double Percent, string ShareText, string CountText, bool IsUncategorized);

/// <summary>One day of the month's transactions, newest day first.</summary>
public sealed record DayGroup(string Title, string TotalText, IReadOnlyList<TransactionRow> Rows);

/// <summary>A notification Helm could not read, on the settings page.</summary>
public sealed class UnreadRow(UnreadNotification notification, string when, Action<UnreadNotification> copy)
{
    public string When { get; } = when;

    public string Bank { get; } = notification.Bank;

    public string Title { get; } = notification.Title;

    public string Text { get; } = notification.Text;

    public IRelayCommand CopyCommand { get; } = new RelayCommand(() => copy(notification));
}

/// <summary>A category on the settings page: rename, hide, or delete (the user's own only).</summary>
public sealed partial class CategoryEditRow : ObservableObject
{
    private readonly WalletViewModel _owner;
    private bool _loading;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isShown;

    internal CategoryEditRow(WalletViewModel owner, CategoryInfo info)
    {
        _owner = owner;
        Id = info.Id;
        _loading = true;
        _name = info.Name;
        _isShown = !info.Hidden;
        _loading = false;
        IsBuiltIn = info.IsBuiltIn;
        KindText = WalletViewModel.KindName(info.Kind);
    }

    public string Id { get; }

    public bool IsBuiltIn { get; }

    public bool CanDelete => !IsBuiltIn;

    public string KindText { get; }

    partial void OnNameChanged(string value)
    {
        if (!_loading) _owner.RenameCategory(this, value);
    }

    partial void OnIsShownChanged(bool value)
    {
        if (!_loading) _owner.SetCategoryShown(Id, value);
    }

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteCategoryAsync(this);

    internal void Reset(CategoryInfo info)
    {
        _loading = true;
        Name = info.Name;
        IsShown = !info.Hidden;
        _loading = false;
    }
}

/// <summary>
/// A transaction on the Wallet page: one tap on a suggested category files it; tap the row to change its category,
/// edit it, see the bank's notification, or delete it.
/// </summary>
public sealed partial class TransactionRow : ObservableObject
{
    private readonly WalletViewModel _owner;
    private bool _loading;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private bool _isIncome;
    [ObservableProperty] private bool _isCategorized;
    [ObservableProperty] private bool _isAuto;
    [ObservableProperty] private string _categoryName = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private CategoryChoice? _selectedChoice;
    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private string _originalText = "";

    // The editor
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editAmount = "";
    [ObservableProperty] private bool _editIsIncome;
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private DateTime? _editDate;
    [ObservableProperty] private string _editNote = "";

    internal TransactionRow(WalletViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    public string Id { get; }

    internal WalletTransaction Transaction { get; private set; } = new();

    public bool IsSpending => !IsIncome;

    public bool HasBalance => BalanceText.Length > 0;

    public bool HasOriginal => OriginalText.Length > 0;

    public string Note => Transaction.Note;

    public bool HasNote => Transaction.Note.Length > 0;

    /// <summary>One-tap categories (only while it waits for one).</summary>
    public ObservableCollection<CategoryChip> Suggestions { get; } = [];

    /// <summary>Every category for this direction (the picker in the expanded row).</summary>
    public ObservableCollection<CategoryChoice> Choices { get; } = [];

    internal void Update(WalletTransaction t, string title, string details, string? categoryName, IReadOnlyList<CategoryInfo> choices)
    {
        _loading = true;
        Transaction = t;
        Title = title;
        Details = details;
        AmountText = WalletFormat.Signed(t.Amount);
        IsIncome = t.Amount > 0;
        IsCategorized = t.IsCategorized;
        IsAuto = t.AutoCategorized;
        CategoryName = categoryName ?? (t.IsCategorized ? "Deleted category" : "");
        BalanceText = t.Balance is { } b ? $"Balance after: {WalletFormat.Money(b)}" : "";
        OriginalText = t.Original;
        var list = choices.Select(c => new CategoryChoice(c.Id, c.Name)).ToList();
        if (!Choices.SequenceEqual(list))
        {
            Choices.Clear();
            foreach (var c in list) Choices.Add(c);
        }
        SelectedChoice = t.CategoryId is { } id ? Choices.FirstOrDefault(c => c.Id == id) : null;
        _loading = false;
        OnPropertyChanged(nameof(IsSpending));
        OnPropertyChanged(nameof(HasBalance));
        OnPropertyChanged(nameof(HasOriginal));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
    }

    internal void SetSuggestions(IReadOnlyList<CategoryInfo> suggestions)
    {
        var same = Suggestions.Count == suggestions.Count && Suggestions.Select(s => s.Id).SequenceEqual(suggestions.Select(s => s.Id))
            && Suggestions.Select(s => s.Name).SequenceEqual(suggestions.Select(s => s.Name));
        if (same) return;
        Suggestions.Clear();
        foreach (var s in suggestions) Suggestions.Add(new CategoryChip(s.Id, s.Name, Pick));
    }

    private void Pick(string categoryId) => _owner.Categorize(this, categoryId);

    partial void OnSelectedChoiceChanged(CategoryChoice? value)
    {
        if (!_loading && value is not null && value.Id != Transaction.CategoryId) _owner.Categorize(this, value.Id);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded) IsEditing = false;
    }

    [RelayCommand]
    private void Uncategorize() => _owner.Categorize(this, null);

    [RelayCommand]
    private void BeginEdit()
    {
        EditAmount = WalletFormat.Money(Transaction.Amount).Replace(" " + WalletFormat.Currency, "");
        EditIsIncome = Transaction.Amount > 0;
        EditDescription = Transaction.Description;
        EditDate = _owner.LocalDate(Transaction.OccurredAt);
        EditNote = Transaction.Note;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SaveEdit()
    {
        if (_owner.SaveEdit(this)) IsEditing = false;
    }

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    [RelayCommand]
    private void CopyOriginal() => _owner.CopyText(OriginalText);
}
