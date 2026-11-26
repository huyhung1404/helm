using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Text;
using Helm.Modules.Vault.Items;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>The login the auto-type picker chose, and whether to offer it in that app next time.</summary>
public sealed record AutoTypeChoice(VaultEntry Entry, bool Remember);

/// <summary>One login in the auto-type picker.</summary>
public sealed partial class AutoTypeRow(VaultEntry entry, bool isLikely) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public VaultEntry Entry { get; } = entry;

    /// <summary>It matched the window in front (listed first while nothing is typed).</summary>
    public bool IsLikely { get; } = isLikely;

    public string Title => Entry.Item.Title;

    public string Username => Entry.Item.Username ?? "";

    public bool HasUsername => Username.Length > 0;

    public string Initial => VaultAvatar.Initial(Entry.Item.Title);

    public string AvatarColor => VaultAvatar.Color(Entry.Item.Title);
}

/// <summary>
/// The auto-type picker (Windows: the shortcut found no single sure login): the likely logins first, then every other
/// one; typing searches all of them. Up and Down move the choice, Enter or a click types it (<see cref="Chosen"/>).
/// </summary>
public sealed partial class AutoTypePickerViewModel : ObservableObject
{
    /// <summary>At most this many rows: the search narrows it down.</summary>
    public const int MaxRows = 30;

    private IReadOnlyList<VaultEntry> _all = [];
    private IReadOnlyList<VaultEntry> _likely = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AutoTypeRow? _selected;
    [ObservableProperty] private string? _rememberText;
    [ObservableProperty] private bool _remember;
    [ObservableProperty] private bool _isEmpty;

    public ObservableCollection<AutoTypeRow> Rows { get; } = [];

    /// <summary>"Offer it in … next time" shows only for an app (a site is matched by its address already).</summary>
    public bool HasRemember => RememberText is not null;

    /// <summary>A login was chosen: the window closes, then it is typed.</summary>
    public event EventHandler<AutoTypeChoice>? Chosen;

    /// <summary>Starts again with these logins, nothing typed, the first row chosen.</summary>
    public void Load(IReadOnlyList<VaultEntry> likely, IReadOnlyList<VaultEntry> all, string? rememberText)
    {
        _likely = likely;
        _all = all.OrderBy(e => e.Item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        RememberText = rememberText;
        Remember = rememberText is not null;
        if (Search.Length == 0) Filter();
        else Search = "";
    }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnRememberTextChanged(string? value) => OnPropertyChanged(nameof(HasRemember));

    partial void OnSelectedChanged(AutoTypeRow? oldValue, AutoTypeRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Up (-1) or Down (+1), stopping at the ends.</summary>
    public void Move(int delta)
    {
        if (Rows.Count == 0) return;
        var index = Selected is null ? -1 : Rows.IndexOf(Selected);
        Selected = Rows[Math.Clamp(index + delta, 0, Rows.Count - 1)];
    }

    /// <summary>Types the given row, or the chosen one (Enter).</summary>
    [RelayCommand]
    private void Choose(AutoTypeRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        Selected = row;
        Chosen?.Invoke(this, new AutoTypeChoice(row.Entry, HasRemember && Remember));
    }

    private void Filter()
    {
        var terms = TextSearch.Terms(Search);
        var likely = _likely.Select(e => e.Uid).ToHashSet(StringComparer.Ordinal);
        var shown = terms.Count == 0
            ? _likely.Concat(_all.Where(e => !likely.Contains(e.Uid))).Take(MaxRows)
            : _all.Where(e => TextSearch.Score(terms, e.Item.Title, e.Item.Username) > 0).Take(MaxRows);
        Rows.Clear();
        foreach (var entry in shown) Rows.Add(new AutoTypeRow(entry, likely.Contains(entry.Uid)));
        Selected = Rows.FirstOrDefault();
        IsEmpty = Rows.Count == 0;
    }
}
