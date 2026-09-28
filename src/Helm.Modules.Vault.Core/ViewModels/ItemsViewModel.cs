using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>The two tabs of the list: what you sign in with, and everything else.</summary>
public enum VaultTab
{
    /// <summary>Logins and tokens: one row each with the icon, the secret hidden until revealed and Copy on the row.</summary>
    LoginsAndTokens,
    /// <summary>Notes, cards, identities, documents and pictures.</summary>
    Other,
}

/// <summary>The "Show" choice inside a tab. <see cref="ItemsViewModel.Filters"/> lists the ones that fit the tab.</summary>
public enum VaultFilter
{
    All,
    Logins,
    Tokens,
    Notes,
    Cards,
    Identities,
    Documents,
    Favorites,
    /// <summary>The tab's deleted items (kept 30 days).</summary>
    Trash,
}

/// <summary>A row of the item list: only what the list shows (never a secret).</summary>
public sealed record ItemRow(string Uid, string Title, string Subtitle, VaultItemKind Kind, bool Favorite, bool HasConflict, int Documents, byte[]? Icon = null)
{
    /// <summary>The picture the user chose for the item; without one the list shows the kind's symbol.</summary>
    public bool HasIcon => Icon is { Length: > 0 };

    public override string ToString() => Title;
}

/// <summary>The unlocked vault: the item list with search and filters, and the selected item.</summary>
public sealed partial class ItemsViewModel : ObservableObject
{
    private readonly VaultStore _store;
    private readonly VaultFiles _files;
    private readonly VaultSession _session;
    private readonly IVaultPlatform _platform;
    private bool _refreshing;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private VaultTab _tab = VaultTab.LoginsAndTokens;
    [ObservableProperty] private VaultFilter _filter = VaultFilter.All;
    [ObservableProperty] private ItemRow? _selected;
    [ObservableProperty] private ItemDetailViewModel? _detail;
    [ObservableProperty] private int _unreadableCount;
    [ObservableProperty] private PasswordRowViewModel? _selectedPassword;

    internal ItemsViewModel(VaultStore store, VaultFiles files, VaultSession session, IVaultPlatform platform)
    {
        _store = store;
        _files = files;
        _session = session;
        _platform = platform;
    }

    public ObservableCollection<ItemRow> Rows { get; } = [];

    /// <summary>The Logins &amp; tokens tab: one row per item, with the secret and Copy on the row.</summary>
    public ObservableCollection<PasswordRowViewModel> PasswordRows { get; } = [];

    public bool IsPasswordsView => Tab == VaultTab.LoginsAndTokens && Filter != VaultFilter.Trash;

    public bool IsListView => !IsPasswordsView;

    public bool IsLoginsTab
    {
        get => Tab == VaultTab.LoginsAndTokens;
        set { if (value) Tab = VaultTab.LoginsAndTokens; }
    }

    public bool IsOtherTab
    {
        get => Tab == VaultTab.Other;
        set { if (value) Tab = VaultTab.Other; }
    }

    private static readonly IReadOnlyList<VaultFilter> LoginFilters = [VaultFilter.All, VaultFilter.Logins, VaultFilter.Tokens, VaultFilter.Favorites, VaultFilter.Trash];

    private static readonly IReadOnlyList<VaultFilter> OtherFilters =
        [VaultFilter.All, VaultFilter.Notes, VaultFilter.Cards, VaultFilter.Identities, VaultFilter.Documents, VaultFilter.Favorites, VaultFilter.Trash];

    /// <summary>The "Show" choices of the current tab.</summary>
    public IReadOnlyList<VaultFilter> Filters => Tab == VaultTab.LoginsAndTokens ? LoginFilters : OtherFilters;

    public IReadOnlyList<VaultItemKind> Kinds { get; } = Enum.GetValues<VaultItemKind>();

    public bool IsEmpty => Rows.Count == 0;

    public bool IsTrash => Filter == VaultFilter.Trash;

    public bool HasUnreadable => UnreadableCount > 0;

    partial void OnUnreadableCountChanged(int value) => OnPropertyChanged(nameof(HasUnreadable));

    /// <summary>Rebuilds the list from the store (after a local save or a sync), keeping the selection.</summary>
    public void Refresh()
    {
        if (_session.State != VaultState.Unlocked)
        {
            Clear();
            return;
        }
        _refreshing = true;
        try
        {
            var keep = Selected?.Uid ?? Detail?.Uid;
            var entries = Filter == VaultFilter.Trash ? _store.Trash() : _store.Items();
            var query = Search.Trim();
            Rows.Clear();
            PasswordRows.Clear();
            foreach (var entry in entries.Where(e => Matches(e, Tab, Filter, query)))
            {
                Rows.Add(new ItemRow(entry.Uid, entry.Item.Title, Subtitle(entry.Item), entry.Item.Kind, entry.Item.Favorite, entry.Conflicts.Count > 0,
                    entry.Item.Attachments.Count, entry.Item.Icon));
                if (IsPasswordsView) PasswordRows.Add(new PasswordRowViewModel(entry.Uid, entry.Item, _platform, _session));
            }
            UnreadableCount = _store.Unreadable().Count;
            var selected = Rows.FirstOrDefault(r => r.Uid == keep);
            Selected = selected;
            // Keep an item being edited open even if the list no longer shows it (e.g. a search that stops matching).
            if (selected is not null && Detail is not { IsEditing: true }) Detail = DetailFor(selected.Uid);
            else if (selected is null && Detail is { IsEditing: false }) Detail = null;
        }
        finally
        {
            _refreshing = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Drops everything decrypted (the vault locked).</summary>
    public void Clear()
    {
        Rows.Clear();
        PasswordRows.Clear();
        Selected = null;
        Detail = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void New(VaultItemKind kind)
    {
        // The new item opens in the tab it will be listed in.
        var tab = TabOf(kind);
        if (Tab != tab) Tab = tab;
        if (Filter == VaultFilter.Trash) Filter = VaultFilter.All;
        Selected = null;
        Detail = new ItemDetailViewModel(_store, _files, _session, _platform, null, kind, OnDetailSaved);
        _session.Touch();
    }

    partial void OnSearchChanged(string value) => Refresh();

    [RelayCommand]
    private void ShowTab(VaultTab tab) => Tab = tab;

    /// <summary>
    /// Clicking the open row again closes it (the next click opens it again). An item being edited stays open, so
    /// nothing typed is lost.
    /// </summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (Detail is { IsEditing: true }) return;
        _refreshing = true;
        try
        {
            SelectedPassword = null;
            Selected = null;
        }
        finally
        {
            _refreshing = false;
        }
        Detail = null;
        _session.Touch();
    }

    public static VaultTab TabOf(VaultItemKind kind) => kind is VaultItemKind.Login or VaultItemKind.Token ? VaultTab.LoginsAndTokens : VaultTab.Other;

    partial void OnTabChanged(VaultTab value)
    {
        OnPropertyChanged(nameof(IsLoginsTab));
        OnPropertyChanged(nameof(IsOtherTab));
        OnPropertyChanged(nameof(Filters));
        if (Filter == VaultFilter.All) Refresh();
        else Filter = VaultFilter.All; // refreshes
        OnPropertyChanged(nameof(IsPasswordsView));
        OnPropertyChanged(nameof(IsListView));
    }

    partial void OnFilterChanged(VaultFilter value)
    {
        OnPropertyChanged(nameof(IsTrash));
        OnPropertyChanged(nameof(IsPasswordsView));
        OnPropertyChanged(nameof(IsListView));
        Refresh();
    }

    /// <summary>Clicking a password row opens the item, like a row of the normal list.</summary>
    partial void OnSelectedPasswordChanged(PasswordRowViewModel? value)
    {
        if (value is null || _refreshing) return;
        Selected = Rows.FirstOrDefault(r => r.Uid == value.Uid);
    }

    partial void OnSelectedChanged(ItemRow? value)
    {
        if (_refreshing) return;
        _session.Touch();
        if (value is null) return;
        if (Detail is { IsEditing: true } editing && editing.Uid != value.Uid)
        {
            // Switching away from an edit keeps nothing unsaved silently: the edit is saved first.
            editing.SaveCommand.Execute(null);
            if (editing.IsEditing) return;
        }
        Detail = DetailFor(value.Uid);
    }

    /// <summary>The detail of an existing item; null when it is gone (e.g. deleted on another device).</summary>
    private ItemDetailViewModel? DetailFor(string uid) =>
        _store.Get(uid) is { } entry ? new ItemDetailViewModel(_store, _files, _session, _platform, entry, entry.Item.Kind, OnDetailSaved) : null;

    /// <summary>After save, trash, restore or delete: refresh and show the item again (null: nothing selected).</summary>
    private void OnDetailSaved(string? uid)
    {
        Detail = null;
        Selected = null;
        Refresh();
        if (uid is null) return;
        Selected = Rows.FirstOrDefault(r => r.Uid == uid);
        // Saved but filtered out of the list (its kind changed, or restored from the trash): still show it.
        if (Selected is null) Detail = DetailFor(uid);
    }

    private static bool Matches(VaultEntry entry, VaultTab tab, VaultFilter filter, string query)
    {
        var item = entry.Item;
        if (TabOf(item.Kind) != tab) return false;
        var kindOk = filter switch
        {
            VaultFilter.Favorites => item.Favorite,
            VaultFilter.Logins => item.Kind == VaultItemKind.Login,
            VaultFilter.Tokens => item.Kind == VaultItemKind.Token,
            VaultFilter.Notes => item.Kind == VaultItemKind.Note,
            VaultFilter.Cards => item.Kind == VaultItemKind.Card,
            VaultFilter.Identities => item.Kind == VaultItemKind.Identity,
            VaultFilter.Documents => item.Kind == VaultItemKind.Document || item.Attachments.Count > 0,
            _ => true,
        };
        if (!kindOk) return false;
        if (query.Length == 0) return true;
        // Search what the list could show anyway: never secret values.
        return item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Attachments.Any(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Fields.Any(f => !f.IsSecret && f.Kind is VaultFieldKind.Username or VaultFieldKind.Url or VaultFieldKind.Email
                && f.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string Subtitle(VaultItem item)
    {
        var first = item.Fields.FirstOrDefault(f => f.Kind is VaultFieldKind.Username or VaultFieldKind.Email && f.Value.Length > 0)?.Value
            ?? item.Fields.FirstOrDefault(f => f.Kind == VaultFieldKind.Url && f.Value.Length > 0)?.Value;
        var documents = item.Attachments.Count switch { 0 => null, 1 => "1 document", var n => $"{n} documents" };
        return Sizes.Join(first ?? item.Kind.ToString(), documents);
    }
}
