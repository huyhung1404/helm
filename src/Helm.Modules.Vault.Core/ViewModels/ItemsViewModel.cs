using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>The two tabs of the list: what you sign in with, and everything else. Each has its own Create.</summary>
public enum VaultTab
{
    /// <summary>Logins and tokens.</summary>
    Credentials,
    /// <summary>Info items: notes, cards, identities, documents and pictures, with fields you add yourself.</summary>
    Other,
}

/// <summary>A sub-tab inside a tab. <see cref="ItemsViewModel.Filters"/> lists the ones of the current tab.</summary>
public enum VaultFilter
{
    All,
    Logins,
    Tokens,
    Favorites,
    /// <summary>The tab's deleted items (kept 30 days).</summary>
    Trash,
}

/// <summary>
/// The unlocked vault: cards for the items of a tab, with search over every tab and sub-tabs. Clicking a card opens its
/// detail below it (clicking again closes it); adding and editing happen in an editor shown above the list
/// (<see cref="IsEditorOpen"/>).
/// </summary>
public sealed partial class ItemsViewModel : ObservableObject
{
    private readonly VaultStore _store;
    private readonly VaultFiles _files;
    private readonly VaultSession _session;
    private readonly IVaultPlatform _platform;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private VaultTab _tab = VaultTab.Credentials;
    [ObservableProperty] private VaultFilter _filter = VaultFilter.All;
    [ObservableProperty] private ItemDetailViewModel? _detail;
    [ObservableProperty] private int _unreadableCount;
    [ObservableProperty] private int _credentialsCount;
    [ObservableProperty] private int _otherCount;

    internal ItemsViewModel(VaultStore store, VaultFiles files, VaultSession session, IVaultPlatform platform)
    {
        _store = store;
        _files = files;
        _session = session;
        _platform = platform;
    }

    /// <summary>The cards of the current tab and sub-tab.</summary>
    public ObservableCollection<VaultCardViewModel> Cards { get; } = [];

    public bool IsCredentialsTab
    {
        get => Tab == VaultTab.Credentials;
        set { if (value) Tab = VaultTab.Credentials; }
    }

    public bool IsOtherTab
    {
        get => Tab == VaultTab.Other;
        set { if (value) Tab = VaultTab.Other; }
    }

    // Favorites first (a star), the trash last (a bin).
    private static readonly IReadOnlyList<VaultFilter> CredentialFilters = [VaultFilter.Favorites, VaultFilter.All, VaultFilter.Logins, VaultFilter.Tokens, VaultFilter.Trash];

    private static readonly IReadOnlyList<VaultFilter> OtherFilters = [VaultFilter.Favorites, VaultFilter.All, VaultFilter.Trash];

    /// <summary>The sub-tabs of the current tab.</summary>
    public IReadOnlyList<VaultFilter> Filters => Tab == VaultTab.Credentials ? CredentialFilters : OtherFilters;

    /// <summary>"Credentials" or "Credentials (3)" while searching, so matches in the other tab are not missed.</summary>
    public string CredentialsHeader => Search.Trim().Length == 0 ? "Credentials" : $"Credentials ({CredentialsCount})";

    public string OtherHeader => Search.Trim().Length == 0 ? "Other" : $"Other ({OtherCount})";

    public bool IsEmpty => Cards.Count == 0;

    public bool IsTrash => Filter == VaultFilter.Trash;

    public bool HasUnreadable => UnreadableCount > 0;

    /// <summary>A new item, or an item being edited: the editor is shown above the list.</summary>
    public bool IsEditorOpen => Detail is { IsEditing: true };

    /// <summary>The card whose detail is open (null: none).</summary>
    public string? ExpandedUid { get; private set; }

    partial void OnUnreadableCountChanged(int value) => OnPropertyChanged(nameof(HasUnreadable));

    partial void OnCredentialsCountChanged(int value) => OnPropertyChanged(nameof(CredentialsHeader));

    partial void OnOtherCountChanged(int value) => OnPropertyChanged(nameof(OtherHeader));

    partial void OnDetailChanged(ItemDetailViewModel? oldValue, ItemDetailViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnDetailPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnDetailPropertyChanged;
        OnPropertyChanged(nameof(IsEditorOpen));
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemDetailViewModel.IsEditing)) OnPropertyChanged(nameof(IsEditorOpen));
    }

    /// <summary>Rebuilds the cards from the store (after a local save or a sync), keeping the open card.</summary>
    public void Refresh()
    {
        if (_session.State != VaultState.Unlocked)
        {
            Clear();
            return;
        }
        var query = Search.Trim();
        var items = _store.Items();
        var entries = Filter == VaultFilter.Trash ? _store.Trash() : items;
        Cards.Clear();
        foreach (var entry in entries.Where(e => Matches(e, Tab, Filter, query)))
            Cards.Add(new VaultCardViewModel(entry, _platform, _session) { IsExpanded = entry.Uid == ExpandedUid });
        CredentialsCount = items.Count(e => Matches(e, VaultTab.Credentials, VaultFilter.All, query));
        OtherCount = items.Count(e => Matches(e, VaultTab.Other, VaultFilter.All, query));
        UnreadableCount = _store.Unreadable().Count;

        // An item being edited stays in the editor even if the list no longer shows it (e.g. a search that stops matching).
        if (Detail is { IsEditing: true }) { }
        else if (ExpandedUid is { } open && Cards.Any(c => c.Uid == open)) Detail = DetailFor(open);
        else Collapse();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Drops everything decrypted (the vault locked).</summary>
    public void Clear()
    {
        Cards.Clear();
        ExpandedUid = null;
        Detail = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Creates an item of the kind (Login or Token in Credentials, Info in Other) in the editor.</summary>
    [RelayCommand]
    private void New(VaultItemKind kind)
    {
        var tab = TabOf(kind);
        if (Tab != tab) Tab = tab;
        if (Filter == VaultFilter.Trash) Filter = VaultFilter.All;
        Detail = new ItemDetailViewModel(_store, _files, _session, _platform, null, kind, OnDetailSaved);
        _session.Touch();
    }

    /// <summary>A click on a card opens its detail below it; a click on the open card closes it.</summary>
    [RelayCommand]
    private void ToggleCard(VaultCardViewModel? card)
    {
        _session.Touch();
        if (card is null) return;
        if (Detail is { IsEditing: true }) return; // the editor is open above the list
        if (card.Uid == ExpandedUid)
        {
            Collapse();
            return;
        }
        Expand(card.Uid);
    }

    /// <summary>Closes the open card (Back on Android).</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (Detail is { IsEditing: true }) return; // nothing typed is lost
        Collapse();
        _session.Touch();
    }

    [RelayCommand]
    private void ShowTab(VaultTab tab) => Tab = tab;

    /// <summary>The star on a card: favorite or not (not a new version of the item).</summary>
    [RelayCommand]
    private void ToggleFavorite(VaultCardViewModel? card)
    {
        if (card is null || card.Trashed) return;
        try
        {
            _store.SetFavorite(card.Uid, !card.Favorite);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Crypto.VaultKeyException)
        {
            return;
        }
        Refresh();
    }

    [RelayCommand]
    private void ShowFilter(VaultFilter filter) => Filter = filter;

    public static VaultTab TabOf(VaultItemKind kind) => kind is VaultItemKind.Login or VaultItemKind.Token ? VaultTab.Credentials : VaultTab.Other;

    partial void OnSearchChanged(string value)
    {
        Refresh();
        OnPropertyChanged(nameof(CredentialsHeader));
        OnPropertyChanged(nameof(OtherHeader));
    }

    partial void OnTabChanged(VaultTab value)
    {
        OnPropertyChanged(nameof(IsCredentialsTab));
        OnPropertyChanged(nameof(IsOtherTab));
        OnPropertyChanged(nameof(Filters));
        if (Detail is not { IsEditing: true }) ExpandedUid = null;
        if (Filter == VaultFilter.All) Refresh();
        else Filter = VaultFilter.All; // refreshes
    }

    partial void OnFilterChanged(VaultFilter value)
    {
        OnPropertyChanged(nameof(IsTrash));
        Refresh();
    }

    private void Expand(string uid)
    {
        ExpandedUid = uid;
        foreach (var c in Cards) c.IsExpanded = c.Uid == uid;
        Detail = DetailFor(uid);
    }

    private void Collapse()
    {
        ExpandedUid = null;
        foreach (var c in Cards) c.IsExpanded = false;
        if (Detail is not { IsEditing: true }) Detail = null;
    }

    /// <summary>The detail of an existing item; null when it is gone (e.g. deleted on another device).</summary>
    private ItemDetailViewModel? DetailFor(string uid) =>
        _store.Get(uid) is { } entry ? new ItemDetailViewModel(_store, _files, _session, _platform, entry, entry.Item.Kind, OnDetailSaved) : null;

    /// <summary>After save, trash, restore or delete: the list is rebuilt and the item's card is open (null: none).</summary>
    private void OnDetailSaved(string? uid)
    {
        Detail = null;
        ExpandedUid = null;
        if (uid is not null && _store.Get(uid) is { } entry)
        {
            // Show the saved item where it now is (its type may have moved it to the other tab).
            var tab = TabOf(entry.Item.Kind);
            if (Tab != tab) Tab = tab;
            if (entry.Trashed != (Filter == VaultFilter.Trash)) Filter = entry.Trashed ? VaultFilter.Trash : VaultFilter.All;
            ExpandedUid = uid;
        }
        Refresh();
        if (ExpandedUid is { } open && Detail is null) Detail = DetailFor(open);
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
            _ => true,
        };
        if (!kindOk) return false;
        if (query.Length == 0) return true;
        // Search what the cards show anyway (an Info card shows its description): never secret values.
        return item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Attachments.Any(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || (tab == VaultTab.Other && item.Notes.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Fields.Any(f => !f.IsSecret && f.Kind is VaultFieldKind.Username or VaultFieldKind.Url or VaultFieldKind.Email
                && f.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
}
