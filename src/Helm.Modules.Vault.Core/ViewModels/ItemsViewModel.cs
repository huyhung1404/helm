using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

public enum VaultFilter
{
    All,
    /// <summary>Every item with a password, one row each, with copy on the row itself.</summary>
    Passwords,
    Favorites,
    Notes,
    Cards,
    Identities,
    Documents,
    Trash,
}

/// <summary>A row of the item list: only what the list shows (never a secret).</summary>
public sealed record ItemRow(string Uid, string Title, string Subtitle, VaultItemKind Kind, bool Favorite, bool HasConflict, int Documents)
{
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

    /// <summary>The Passwords view: one row per item with a password, with copy on the row.</summary>
    public ObservableCollection<PasswordRowViewModel> PasswordRows { get; } = [];

    public bool IsPasswordsView => Filter == VaultFilter.Passwords;

    public bool IsListView => Filter != VaultFilter.Passwords;

    public IReadOnlyList<VaultFilter> Filters { get; } = Enum.GetValues<VaultFilter>();

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
            foreach (var entry in entries.Where(e => Matches(e, Filter, query)))
            {
                Rows.Add(new ItemRow(entry.Uid, entry.Item.Title, Subtitle(entry.Item), entry.Item.Kind, entry.Item.Favorite, entry.Conflicts.Count > 0,
                    entry.Item.Attachments.Count));
                if (Filter == VaultFilter.Passwords) PasswordRows.Add(new PasswordRowViewModel(entry.Uid, entry.Item, _platform, _session));
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
        if (Filter == VaultFilter.Trash) Filter = VaultFilter.All;
        Selected = null;
        Detail = new ItemDetailViewModel(_store, _files, _session, _platform, null, kind, OnDetailSaved);
        _session.Touch();
    }

    partial void OnSearchChanged(string value) => Refresh();

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
        // Saved but filtered out of the list (another kind, or restored from the trash): still show it.
        if (Selected is null) Detail = DetailFor(uid);
    }

    private static bool Matches(VaultEntry entry, VaultFilter filter, string query)
    {
        var item = entry.Item;
        var kindOk = filter switch
        {
            VaultFilter.Favorites => item.Favorite,
            VaultFilter.Passwords => item.Kind == VaultItemKind.Login || item.Password is { Length: > 0 },
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
