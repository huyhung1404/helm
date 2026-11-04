using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Modules.Wallet;

/// <summary>Photos for savings goals: picked (and on the phone taken) by the platform, already made small (JPEG).</summary>
public interface IGoalPhotos
{
    /// <summary>The phone has a camera to take one with.</summary>
    bool CanTakePhoto { get; }

    /// <summary>A picture from the computer's files or the phone's gallery; null when the user cancelled.</summary>
    Task<byte[]?> PickAsync(CancellationToken ct = default);

    /// <summary>A new photo from the camera (the phone); null when the user cancelled.</summary>
    Task<byte[]?> TakeAsync(CancellationToken ct = default);
}

/// <summary>Money put in or taken out of a goal, in its pop-up's history.</summary>
public sealed record GoalEntryRow(string DayText, string AmountText, bool IsOut);

/// <summary>
/// A savings goal on the Wallet page: its photo lights up from the bottom as money is set aside (<see cref="Share"/>).
/// Kept across refreshes, so the light rises smoothly after money is set aside.
/// </summary>
public sealed partial class GoalRow : ObservableObject
{
    private readonly Action<GoalRow> _open;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private GoalState _state;

    /// <summary>Saved as a share of the price, 0–1: how far the photo is lit.</summary>
    [ObservableProperty] private double _share;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _savedText = "";
    [ObservableProperty] private string _savedShort = "";
    [ObservableProperty] private string _priceText = "";
    [ObservableProperty] private string _priceShort = "";

    /// <summary>"8,640,000 ₫ to go", or "Enough saved".</summary>
    [ObservableProperty] private string _leftText = "";
    [ObservableProperty] private string _leftShort = "";

    /// <summary>"~720,000 ₫ a week until 31 Jan"; empty without a date.</summary>
    [ObservableProperty] private string _paceText = "";

    /// <summary>A finished goal's line: "Bought for 12,990,000 ₫ · 4 Nov" or "Given up 4 Nov · 4,350,000 ₫ back".</summary>
    [ObservableProperty] private string _endedText = "";
    [ObservableProperty] private int _color;

    /// <summary>The photo's JPEG; null until it is read (or when there is none): the colour and the first letter show.</summary>
    [ObservableProperty] private byte[]? _photo;

    internal GoalRow(string id, Action<GoalRow> open)
    {
        Id = id;
        _open = open;
    }

    public string Id { get; }

    internal WalletGoal Goal { get; private set; } = null!;

    /// <summary>The blob the photo was read from (read again only when it changes).</summary>
    internal string? PhotoId { get; set; }

    public ObservableCollection<GoalEntryRow> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    /// <summary><see cref="Share"/> as 0–100, for a progress bar.</summary>
    public double Percent => Share * 100;

    public bool HasPhoto => Photo is not null;

    public bool HasPace => PaceText.Length > 0;

    /// <summary>The first letter of the name, on the coloured tile of a goal without a photo.</summary>
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], CultureInfo.CurrentCulture).ToString() : "?";

    public bool IsSaving => State == GoalState.Saving;

    public bool IsBought => State == GoalState.Bought;

    /// <summary>Still saving and not there yet: "Set aside" is the main button.</summary>
    public bool CanPut => IsSaving && !IsReady;

    /// <summary>Saving and there is enough: "I bought it" is the main button.</summary>
    public bool ShowBuy => IsSaving && IsReady;

    public bool HasSaved => Goal?.Saved > 0;

    public string StateText => State switch { GoalState.Bought => "Bought", GoalState.GaveUp => "Given up", _ => IsReady ? "Ready" : "" };

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initial));

    partial void OnShareChanged(double value) => OnPropertyChanged(nameof(Percent));

    partial void OnPhotoChanged(byte[]? value) => OnPropertyChanged(nameof(HasPhoto));

    partial void OnPaceTextChanged(string value) => OnPropertyChanged(nameof(HasPace));

    partial void OnStateChanged(GoalState value) => Changed();

    partial void OnIsReadyChanged(bool value) => Changed();

    private void Changed()
    {
        foreach (var name in (string[])[nameof(IsSaving), nameof(IsBought), nameof(CanPut), nameof(ShowBuy), nameof(StateText)]) OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Open() => _open(this);

    internal void Update(WalletGoal goal, DateOnly today, TimeZoneInfo zone)
    {
        Goal = goal;
        Name = goal.Name;
        State = goal.State;
        Share = goal.Share;
        IsReady = goal.IsReady;
        PercentText = GoalMath.Percent(goal);
        SavedText = WalletFormat.Money(goal.Saved);
        SavedShort = WalletFormat.Short(goal.Saved);
        PriceText = WalletFormat.Money(goal.Price);
        PriceShort = WalletFormat.Short(goal.Price);
        LeftText = goal.IsReady ? "Enough saved" : $"{WalletFormat.Money(goal.Left)} to go";
        LeftShort = goal.IsReady ? "Enough saved" : $"{WalletFormat.Short(goal.Left)} to go";
        PaceText = GoalMath.PerWeek(goal, today) is { } week && goal.Due is { } due
            ? $"~{WalletFormat.Money(week)} a week until {due.ToString("d MMM", CultureInfo.CurrentCulture)}"
            : goal.Due is { } late && goal.IsSaving && !goal.IsReady && late < today ? $"Meant for {late.ToString("d MMM", CultureInfo.CurrentCulture)}" : "";
        var ended = goal.EndedAt is { } at ? WalletFormat.LocalDay(at, zone).ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
        EndedText = goal.State switch
        {
            GoalState.Bought => $"Bought for {WalletFormat.Money(goal.Paid ?? goal.Price)} · {ended}",
            GoalState.GaveUp => $"Given up {ended} · {WalletFormat.Money(goal.Saved)} went back to the balance",
            _ => "",
        };
        Color = goal.Color;
        var entries = goal.Entries.Reverse()
            .Select(e => new GoalEntryRow(WalletFormat.LocalDay(e.At, zone).ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                (e.Amount < 0 ? "−" : "+") + WalletFormat.Money(Math.Abs(e.Amount)), e.Amount < 0))
            .ToList();
        if (!Entries.SequenceEqual(entries))
        {
            Entries.Clear();
            foreach (var e in entries) Entries.Add(e);
        }
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasSaved));
    }
}

/// <summary>
/// The Wallet's savings goals, shared by the Windows and Android pages: the goals and their photos, the balance after the
/// money they hold, setting money aside and taking it out, buying, giving up (each with Undo), and the goal form. Part of
/// <see cref="WalletViewModel"/> (it refreshes them with the page); used on the UI thread.
/// </summary>
public sealed partial class GoalsViewModel : ObservableObject
{
    /// <summary>The quick amounts beside the amount box.</summary>
    public static IReadOnlyList<decimal> QuickAmounts { get; } = [50_000, 100_000, 200_000, 500_000];

    private readonly GoalBook _book;
    private readonly WalletViewModel _owner;
    private readonly IDialogService _dialogs;
    private readonly IGoalPhotos? _photos;
    private readonly ISettingsStore<WalletSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, GoalRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _photoCache = new(StringComparer.Ordinal);
    private Func<Task>? _undo;
    private decimal _available;

    [ObservableProperty] private GoalRow? _selected;
    [ObservableProperty] private string _availableText = "";
    [ObservableProperty] private bool _isAvailableNegative;
    [ObservableProperty] private string _setAsideText = "";

    /// <summary>What the last set-aside, purchase or give-up did, with Undo; empty when there is nothing to undo.</summary>
    [ObservableProperty] private string _undoText = "";

    // Setting money aside (the goal's pop-up)
    [ObservableProperty] private string _putAmount = "";

    // Taking money out
    [ObservableProperty] private string _takeAmount = "";

    // Buying it
    [ObservableProperty] private string _boughtAmount = "";

    // The goal form
    [ObservableProperty] private string? _formId;
    [ObservableProperty] private string _formName = "";
    [ObservableProperty] private string _formPrice = "";
    [ObservableProperty] private DateTime? _formDue;
    [ObservableProperty] private string _formStart = "";
    [ObservableProperty] private byte[]? _formPhoto;
    private bool _formPhotoChanged;

    internal GoalsViewModel(GoalBook book, WalletViewModel owner, IDialogService dialogs, IGoalPhotos? photos, ISettingsStore<WalletSettings> settings,
        IUiDispatcher ui, TimeZoneInfo zone, bool hasWidget)
    {
        _book = book;
        _owner = owner;
        _dialogs = dialogs;
        _photos = photos;
        _settings = settings;
        _ui = ui;
        _zone = zone;
        HasWidget = hasWidget;
    }

    internal GoalBook Book => _book;

    /// <summary>Raised after the goal the widget shows, or its money, changed: the phone redraws its Goal widgets.</summary>
    public event EventHandler? WidgetChanged;

    /// <summary>The goals being saved for, in page order.</summary>
    public ObservableCollection<GoalRow> Rows { get; } = [];

    /// <summary>The goals bought or given up, latest first.</summary>
    public ObservableCollection<GoalRow> Finished { get; } = [];

    public bool HasGoals => Rows.Count > 0;

    public bool HasNoGoals => Rows.Count == 0 && Finished.Count == 0;

    public bool HasFinished => Finished.Count > 0;

    public bool HasSetAside => SetAsideText.Length > 0;

    public bool HasUndo => UndoText.Length > 0;

    /// <summary>Goals can have photos here (the sync blob store is there).</summary>
    public bool CanHavePhotos => _photos is not null && _book.HasPhotos;

    public bool CanTakePhoto => CanHavePhotos && _photos!.CanTakePhoto;

    /// <summary>The phone, where a home-screen widget shows one goal.</summary>
    public bool HasWidget { get; }

    /// <summary>The selected goal is the one the widget shows.</summary>
    public bool SelectedIsOnWidget => Selected is { IsSaving: true } s && WidgetGoalId() == s.Id;

    public bool CanShowSelectedOnWidget => HasWidget && Selected is { IsSaving: true } && !SelectedIsOnWidget;

    partial void OnSetAsideTextChanged(string value) => OnPropertyChanged(nameof(HasSetAside));

    partial void OnUndoTextChanged(string value) => OnPropertyChanged(nameof(HasUndo));

    partial void OnSelectedChanged(GoalRow? value)
    {
        OnPropertyChanged(nameof(SelectedIsOnWidget));
        OnPropertyChanged(nameof(CanShowSelectedOnWidget));
        PutChanged();
    }

    // ---- Refresh -------------------------------------------------------------------------------------------------

    internal void Update()
    {
        var today = WalletFormat.LocalDay(_book.Wallet.Now, _zone);
        var all = _book.All();
        var saving = new List<GoalRow>();
        var finished = new List<GoalRow>();
        foreach (var g in all)
        {
            if (!_rows.TryGetValue(g.Id, out var row)) _rows[g.Id] = row = new GoalRow(g.Id, Open);
            row.Update(g.Value, today, _zone);
            ShowPhoto(row, g.Value.Photo);
            (g.Value.IsSaving ? saving : finished).Add(row);
        }
        foreach (var id in _rows.Keys.Where(id => all.All(g => g.Id != id)).ToList()) _rows.Remove(id);
        Fill(Rows, saving);
        Fill(Finished, finished);
        OnPropertyChanged(nameof(HasGoals));
        OnPropertyChanged(nameof(HasNoGoals));
        OnPropertyChanged(nameof(HasFinished));
        if (Selected is { } selected && !_rows.ContainsKey(selected.Id)) Selected = null;

        _available = _book.Available(_zone);
        IsAvailableNegative = _available < 0;
        AvailableText = (_available < 0 ? "−" : "") + WalletFormat.Money(Math.Abs(_available));
        var held = _book.SetAside();
        var count = saving.Count(r => r.Goal.Saved > 0);
        SetAsideText = held > 0 ? $"{WalletFormat.Money(held)} set aside in {WalletFormat.Count(count, "goal")} · still yours" : "";
        PutChanged();
        OnPropertyChanged(nameof(SelectedIsOnWidget));
        OnPropertyChanged(nameof(CanShowSelectedOnWidget));
    }

    private static void Fill(ObservableCollection<GoalRow> target, List<GoalRow> rows)
    {
        if (target.SequenceEqual(rows)) return;
        target.Clear();
        foreach (var r in rows) target.Add(r);
    }

    /// <summary>The photo from the cache, or read from the blob store in the background (once per picture).</summary>
    private void ShowPhoto(GoalRow row, BlobRef? photo)
    {
        if (photo is null)
        {
            row.PhotoId = null;
            row.Photo = null;
            return;
        }
        if (row.PhotoId == photo.Id && row.Photo is not null) return;
        row.PhotoId = photo.Id;
        if (_photoCache.TryGetValue(photo.Id, out var cached))
        {
            row.Photo = cached;
            return;
        }
        _ = Task.Run(async () =>
        {
            if (await _book.ReadPhotoAsync(photo).ConfigureAwait(false) is not { } bytes) return;
            _ui.Post(() =>
            {
                _photoCache[photo.Id] = bytes;
                if (row.PhotoId == photo.Id) row.Photo = bytes;
            });
        });
    }

    // ---- A goal --------------------------------------------------------------------------------------------------

    internal void Open(GoalRow row)
    {
        Selected = row;
        PutAmount = "";
        _owner.ShowPopup(WalletPopup.Goal, stack: false);
    }

    /// <summary>Opens a goal by its id (the widget); false when it is gone.</summary>
    internal bool Open(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) return false;
        Open(row);
        return true;
    }

    public string PutHint
    {
        get
        {
            if (Selected?.Goal is not { } g) return "";
            if (!WalletText.TryParseAmount(PutAmount, out var amount) || amount <= 0) return $"Available {AvailableText}";
            var put = Math.Min(amount, g.Left);
            if (put > _available) return _available > 0 ? $"Only {WalletFormat.Money(_available)} is available" : "There is no money available to set aside";
            var after = GoalMath.Percent(g with { Entries = [.. g.Entries, new GoalEntry { Amount = put }] });
            return amount > g.Left ? $"Only {WalletFormat.Money(g.Left)} to go: that much is set aside ({after})" : $"= {WalletFormat.Money(put)} · {after} after";
        }
    }

    /// <summary>The amount typed is more than the balance has.</summary>
    public bool PutTooMuch => Selected?.Goal is { } g && WalletText.TryParseAmount(PutAmount, out var a) && Math.Min(a, g.Left) > _available;

    /// <summary>Where the bar would be after setting aside what is typed, 0–100 (drawn behind the saved part).</summary>
    public double PutPreviewPercent
    {
        get
        {
            if (Selected?.Goal is not { } g) return 0;
            var put = WalletText.TryParseAmount(PutAmount, out var a) && a > 0 ? Math.Min(a, g.Left) : 0;
            return g.Price <= 0 ? 0 : (double)Math.Clamp((g.Saved + put) * 100 / g.Price, 0, 100);
        }
    }

    public double SelectedPercent => (Selected?.Share ?? 0) * 100;

    /// <summary>"The rest · 250K": puts in all that is missing.</summary>
    public string RestText => Selected?.Goal is { Left: > 0 } g ? "The rest · " + WalletFormat.Short(g.Left) : "";

    public bool HasRest => RestText.Length > 0;

    partial void OnPutAmountChanged(string value)
    {
        PutChanged();
        _owner.PopupError = null;
    }

    private void PutChanged()
    {
        foreach (var name in (string[])[nameof(PutHint), nameof(PutTooMuch), nameof(PutPreviewPercent), nameof(SelectedPercent), nameof(RestText), nameof(HasRest)])
            OnPropertyChanged(name);
    }

    /// <summary>A quick amount adds to what is typed ("+100K" twice is 200,000).</summary>
    [RelayCommand]
    private void AddQuick(string? amount)
    {
        if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var add)) return;
        var now = WalletText.TryParseAmount(PutAmount, out var typed) && typed > 0 ? typed : 0;
        PutAmount = (now + add).ToString("#,0", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void PutRest()
    {
        if (Selected?.Goal is { Left: > 0 } g) PutAmount = g.Left.ToString("#,0", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void SavePut()
    {
        if (Selected is not { } row) return;
        if (!WalletText.TryParseAmount(PutAmount, out var amount) || amount <= 0)
        {
            _owner.PopupError = "Enter the amount to set aside, e.g. 200k.";
            return;
        }
        try
        {
            var wasReady = row.Goal.IsReady;
            var entry = _book.Put(row.Id, amount, _zone);
            PutAmount = "";
            Changed();
            var ready = _book.Get(row.Id)?.IsReady == true;
            Offer($"Set aside {WalletFormat.Money(entry.Amount)} for {row.Name}.", () => _book.Remove(row.Id, entry));
            _owner.ShowToast(ready && !wasReady ? $"All saved for {row.Name}: go get it!" : $"Set aside {WalletFormat.Money(entry.Amount)} · {GoalMath.Percent(_book.Get(row.Id)!)}");
            if (ready) _owner.ClosePopupCommand.Execute(null);
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    // ---- Taking money out ----------------------------------------------------------------------------------------

    public string TakeHint => Selected?.Goal is not { } g ? ""
        : WalletText.TryParseAmount(TakeAmount, out var a) && a > 0
            ? a > g.Saved ? $"Only {WalletFormat.Money(g.Saved)} is set aside" : $"= {WalletFormat.Money(a)}, back in your balance at once"
            : $"{WalletFormat.Money(g.Saved)} is set aside";

    partial void OnTakeAmountChanged(string value)
    {
        OnPropertyChanged(nameof(TakeHint));
        _owner.PopupError = null;
    }

    [RelayCommand]
    private void OpenTakeOut()
    {
        if (Selected is null) return;
        TakeAmount = "";
        _owner.ShowPopup(WalletPopup.GoalTakeOut);
    }

    [RelayCommand]
    private void SaveTake()
    {
        if (Selected is not { } row) return;
        if (!WalletText.TryParseAmount(TakeAmount, out var amount) || amount <= 0)
        {
            _owner.PopupError = "Enter the amount to take out.";
            return;
        }
        try
        {
            var entry = _book.TakeOut(row.Id, amount);
            Changed();
            Offer($"Took {WalletFormat.Money(amount)} out of {row.Name}: it is back in your balance.", () => _book.Remove(row.Id, entry));
            _owner.DismissPopupCommand.Execute(null);
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    // ---- Buying it -----------------------------------------------------------------------------------------------

    /// <summary>The spending category of the purchase (Shopping first).</summary>
    public ObservableCollection<CategoryPick> BoughtPicks { get; } = [];

    public string BoughtHint
    {
        get
        {
            if (Selected?.Goal is not { } g) return "";
            if (!WalletText.TryParseAmount(BoughtAmount, out var paid) || paid <= 0) return "Type what you paid";
            var rest = g.Saved - paid;
            return rest > 0 ? $"One spending of {WalletFormat.Money(paid)}. The {WalletFormat.Money(rest)} left over goes back to your balance."
                : rest < 0 ? $"One spending of {WalletFormat.Money(paid)}. The extra {WalletFormat.Money(-rest)} comes off your balance."
                : $"One spending of {WalletFormat.Money(paid)}, all from the money set aside.";
        }
    }

    partial void OnBoughtAmountChanged(string value)
    {
        OnPropertyChanged(nameof(BoughtHint));
        _owner.PopupError = null;
    }

    [RelayCommand]
    private void OpenBought()
    {
        if (Selected?.Goal is not { } g) return;
        BoughtAmount = g.Price.ToString("#,0", CultureInfo.CurrentCulture);
        BoughtPicks.Clear();
        var expenses = _book.Wallet.Categories().Where(c => c.Kind == CategoryKind.Expense).ToList();
        foreach (var c in expenses.OrderBy(c => c.Id == WalletCategories.Shopping ? 0 : 1))
            BoughtPicks.Add(new CategoryPick(c.Id, c.Name, c.Icon, c.Color, PickBought) { IsSelected = c.Id == WalletCategories.Shopping });
        if (BoughtPicks.Count > 0 && !BoughtPicks.Any(p => p.IsSelected)) BoughtPicks[0].IsSelected = true;
        _owner.ShowPopup(WalletPopup.GoalBought);
    }

    private void PickBought(CategoryPick pick)
    {
        foreach (var p in BoughtPicks) p.IsSelected = p == pick;
    }

    [RelayCommand]
    private void SaveBought()
    {
        if (Selected is not { } row) return;
        if (!WalletText.TryParseAmount(BoughtAmount, out var paid) || paid <= 0)
        {
            _owner.PopupError = "Enter what it cost.";
            return;
        }
        var category = BoughtPicks.FirstOrDefault(p => p.IsSelected);
        try
        {
            _book.Buy(row.Id, paid, category?.Id, _zone);
            Changed();
            Offer($"Bought {row.Name} for {WalletFormat.Money(paid)}{(category is null ? "" : " · " + category.Name)}. Enjoy it!", () => _book.Reopen(row.Id));
            _owner.ClosePopupCommand.Execute(null);
            _owner.Refresh();
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    // ---- Giving up, deleting -------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task GiveUpSelectedAsync()
    {
        if (Selected is not { IsSaving: true } row) return;
        var saved = row.Goal.Saved;
        var message = saved > 0 ? $"The {WalletFormat.Money(saved)} set aside goes back to your balance. You can undo it right after." : "Nothing is set aside for it yet.";
        if (!await _dialogs.ConfirmAsync($"Give up {row.Name}?", message, "Give up")) return;
        try
        {
            _book.GiveUp(row.Id);
            Changed();
            Offer(saved > 0 ? $"Gave up {row.Name}: {WalletFormat.Money(saved)} is back in your balance." : $"Gave up {row.Name}.", () => _book.Reopen(row.Id));
            _owner.ClosePopupCommand.Execute(null);
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    /// <summary>Deletes a bought or given-up goal from the list (a purchase's transaction stays).</summary>
    [RelayCommand]
    private async Task DeleteFinishedAsync(GoalRow? row)
    {
        row ??= Selected;
        if (row is not { IsSaving: false }) return;
        var note = row.IsBought ? "Its purchase stays in your transactions." : "No money moves.";
        if (!await _dialogs.ConfirmAsync($"Delete {row.Name}?", note, "Delete")) return;
        try
        {
            _book.Delete(row.Id);
            UndoText = "";
            _undo = null;
            if (Selected == row) _owner.ClosePopupCommand.Execute(null);
            Changed();
            _owner.ShowToast($"Deleted {row.Name}");
        }
        catch (InvalidOperationException ex)
        {
            _owner.Message = ex.Message;
        }
    }

    /// <summary>The goal is saved already; its photo follows (stored encrypted, synced with the next run).</summary>
    private async Task SavePhotoAsync(string id, byte[] photo)
    {
        try
        {
            await _book.SetPhotoAsync(id, photo);
            if (_book.Get(id)?.Photo is { } blob) _photoCache[blob.Id] = photo;
            Changed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BlobTooLargeException or SyncAuthException or SyncRequestException)
        {
            _owner.Message = "The goal is saved, but its photo could not be: " + ex.Message;
        }
    }

    // ---- Undo ----------------------------------------------------------------------------------------------------

    private void Offer(string text, Func<bool> undo)
    {
        UndoText = text;
        _undo = () =>
        {
            undo();
            return Task.CompletedTask;
        };
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_undo is not { } undo) return;
        _undo = null;
        UndoText = "";
        await undo();
        Changed();
        _owner.Refresh();
        _owner.ShowToast("Undone");
    }

    [RelayCommand]
    private void DismissUndo()
    {
        _undo = null;
        UndoText = "";
    }

    // ---- The widget ----------------------------------------------------------------------------------------------

    private string? WidgetGoalId() => _settings.Current.WidgetGoalId is { } id && _book.Get(id) is { IsSaving: true } ? id : _book.Saving().FirstOrDefault()?.Id;

    [RelayCommand]
    private void ShowSelectedOnWidget()
    {
        if (Selected is not { IsSaving: true } row) return;
        _settings.Update(s => s.WidgetGoalId = row.Id);
        OnPropertyChanged(nameof(SelectedIsOnWidget));
        OnPropertyChanged(nameof(CanShowSelectedOnWidget));
        WidgetChanged?.Invoke(this, EventArgs.Empty);
        _owner.ShowToast($"The Goal widget shows {row.Name}");
    }

    /// <summary>The goals changed here: the page and the widget follow.</summary>
    private void Changed()
    {
        Update();
        WidgetChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- The goal form -------------------------------------------------------------------------------------------

    public string FormTitle => FormId is null ? "New goal" : "Edit goal";

    public string FormSaveText => FormId is null ? "Start saving" : "Save";

    public bool FormIsNew => FormId is null;

    public bool HasFormPhoto => FormPhoto is not null;

    public string FormPriceHint => WalletText.TryParseAmount(FormPrice, out var a) && a > 0 ? "= " + WalletFormat.Money(a) : "Type 12tr, 12.990.000";

    public string FormStartHint => WalletText.TryParseAmount(FormStart, out var a) && a > 0
        ? $"= {WalletFormat.Money(a)}, set aside now (available {AvailableText})"
        : "Money you already keep for it; it comes off your balance now";

    public string FormPaceHint
    {
        get
        {
            if (FormDue is not { } due || !WalletText.TryParseAmount(FormPrice, out var price) || price <= 0) return "";
            var start = WalletText.TryParseAmount(FormStart, out var s) ? s : 0;
            var saved = FormId is { } id && _book.Get(id) is { } g ? g.Saved : start;
            var goal = new WalletGoal { Price = price, Due = DateOnly.FromDateTime(due), Entries = [new GoalEntry { Amount = saved }] };
            return GoalMath.PerWeek(goal, WalletFormat.LocalDay(_book.Wallet.Now, _zone)) is { } week ? $"About {WalletFormat.Money(week)} a week to have it by then." : "";
        }
    }

    public bool HasFormPaceHint => FormPaceHint.Length > 0;

    partial void OnFormIdChanged(string? value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSaveText));
        OnPropertyChanged(nameof(FormIsNew));
    }

    partial void OnFormPriceChanged(string value) => FormChanged();

    partial void OnFormStartChanged(string value) => FormChanged();

    partial void OnFormDueChanged(DateTime? value) => FormChanged();

    partial void OnFormPhotoChanged(byte[]? value) => OnPropertyChanged(nameof(HasFormPhoto));

    private void FormChanged()
    {
        foreach (var name in (string[])[nameof(FormPriceHint), nameof(FormStartHint), nameof(FormPaceHint), nameof(HasFormPaceHint)]) OnPropertyChanged(name);
        _owner.PopupError = null;
    }

    [RelayCommand]
    private void OpenAdd()
    {
        FormId = null;
        FormName = "";
        FormPrice = "";
        FormDue = null;
        FormStart = "";
        FormPhoto = null;
        _formPhotoChanged = false;
        _owner.ShowPopup(WalletPopup.GoalForm, stack: false);
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is not { Goal: var g } row) return;
        FormId = row.Id;
        FormName = g.Name;
        FormPrice = g.Price.ToString("#,0", CultureInfo.CurrentCulture);
        FormDue = g.Due?.ToDateTime(TimeOnly.MinValue);
        FormStart = "";
        FormPhoto = row.Photo;
        _formPhotoChanged = false;
        _owner.ShowPopup(WalletPopup.GoalForm);
    }

    [RelayCommand]
    private async Task PickPhotoAsync() => await TakePictureAsync(camera: false);

    [RelayCommand]
    private async Task TakePhotoAsync() => await TakePictureAsync(camera: true);

    private async Task TakePictureAsync(bool camera)
    {
        if (_photos is null) return;
        try
        {
            if (await (camera ? _photos.TakeAsync() : _photos.PickAsync()) is not { } picture) return;
            FormPhoto = picture;
            _formPhotoChanged = true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            _owner.PopupError = "That picture could not be read: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveFormPhoto()
    {
        FormPhoto = null;
        _formPhotoChanged = true;
    }

    [RelayCommand]
    private async Task SaveFormAsync()
    {
        if (FormName.Trim().Length == 0)
        {
            _owner.PopupError = "Name what you are saving for.";
            return;
        }
        if (!WalletText.TryParseAmount(FormPrice, out var price) || price <= 0)
        {
            _owner.PopupError = "Enter its price, e.g. 12tr.";
            return;
        }
        var start = 0m;
        if (FormIsNew && FormStart.Trim().Length > 0 && (!WalletText.TryParseAmount(FormStart, out start) || start < 0))
        {
            _owner.PopupError = "Enter the money already saved, or leave it empty.";
            return;
        }
        DateOnly? due = FormDue is { } d ? DateOnly.FromDateTime(d) : null;
        try
        {
            string id;
            if (FormId is { } edited)
            {
                _book.Update(edited, FormName, price, due);
                id = edited;
            }
            else
            {
                id = _book.Add(FormName, price, due, start, _zone);
            }
            var photo = _formPhotoChanged ? FormPhoto : null;
            var removed = _formPhotoChanged && FormPhoto is null;
            var isNew = FormIsNew;
            _owner.ClosePopupCommand.Execute(null);
            Changed();
            _owner.ShowToast(isNew ? $"Saving for {FormName.Trim()}" : "Goal saved");
            if (removed) _book.RemovePhoto(id);
            else if (photo is not null) await SavePhotoAsync(id, photo);
        }
        catch (ArgumentException ex)
        {
            _owner.PopupError = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }
}
