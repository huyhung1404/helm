using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>What a section of the vault lists.</summary>
public enum VaultSectionKind
{
    All,
    Favorites,
    Logins,
    /// <summary>Tokens, and every item with a two-factor code.</summary>
    Tokens,
    Cards,
    Identities,
    Notes,
    /// <summary>Documents, and every item with a file.</summary>
    Documents,
    /// <summary>The items with one tag (<see cref="VaultSection.Tag"/>).</summary>
    Tag,
    /// <summary>Deleted items, kept 30 days.</summary>
    Trash,
}

/// <summary>One entry of the vault's navigation (left column on the PC, the chip row on a phone), with its count.</summary>
public sealed partial class VaultSection : ObservableObject
{
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    public VaultSection(VaultSectionKind kind, string label, string? tag = null)
    {
        Kind = kind;
        Label = label;
        Tag = tag;
    }

    public VaultSectionKind Kind { get; }

    public string Label { get; }

    /// <summary>The tag of a <see cref="VaultSectionKind.Tag"/> section.</summary>
    public string? Tag { get; }

    public bool IsTag => Kind == VaultSectionKind.Tag;

    /// <summary>A live (not trashed) item belongs here.</summary>
    internal bool Contains(VaultItem item) => Kind switch
    {
        VaultSectionKind.All => true,
        VaultSectionKind.Favorites => item.Favorite,
        VaultSectionKind.Logins => item.Kind == VaultItemKind.Login,
        VaultSectionKind.Tokens => item.Kind == VaultItemKind.Token || item.Fields.Any(f => f.Kind == VaultFieldKind.Totp && f.Value.Length > 0),
        VaultSectionKind.Cards => item.Kind == VaultItemKind.Card,
        VaultSectionKind.Identities => item.Kind == VaultItemKind.Identity,
        VaultSectionKind.Notes => item.Kind == VaultItemKind.Note,
        VaultSectionKind.Documents => item.Kind == VaultItemKind.Document || item.Attachments.Count > 0,
        VaultSectionKind.Tag => item.Tags.Any(t => string.Equals(t.Trim(), Tag, StringComparison.CurrentCultureIgnoreCase)),
        _ => false,
    };

    public override string ToString() => Label;
}

/// <summary>
/// The unlocked vault: sections (kinds, favorites, tags, trash), the items of the chosen section with search, the
/// selected item's detail (read only), and the editor where items are added and changed (<see cref="Editor"/>, shown as
/// a pop-up over the vault).
/// </summary>
public sealed partial class ItemsViewModel : ObservableObject
{
    private readonly VaultStore _store;
    private readonly VaultFiles _files;
    private readonly VaultSession _session;
    private readonly IVaultPlatform _platform;
    private bool _refreshing;
    private bool _quietSection;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private VaultSection _section;
    [ObservableProperty] private VaultCardViewModel? _selectedCard;
    [ObservableProperty] private ItemDetailViewModel? _detail;
    [ObservableProperty] private ItemDetailViewModel? _editor;
    [ObservableProperty] private int _unreadableCount;

    internal ItemsViewModel(VaultStore store, VaultFiles files, VaultSession session, IVaultPlatform platform)
    {
        _store = store;
        _files = files;
        _session = session;
        _platform = platform;
        Sections =
        [
            new(VaultSectionKind.All, "All items"),
            new(VaultSectionKind.Favorites, "Favorites"),
            new(VaultSectionKind.Logins, "Logins"),
            new(VaultSectionKind.Tokens, "2FA & tokens"),
            new(VaultSectionKind.Cards, "Cards"),
            new(VaultSectionKind.Identities, "Identities"),
            new(VaultSectionKind.Notes, "Notes"),
            new(VaultSectionKind.Documents, "Documents"),
        ];
        TrashSection = new VaultSection(VaultSectionKind.Trash, "Trash");
        _section = Sections[0];
        _section.IsSelected = true;
    }

    /// <summary>The fixed sections, in the order they are shown.</summary>
    public IReadOnlyList<VaultSection> Sections { get; }

    /// <summary>One section per tag used by a live item, by name.</summary>
    public ObservableCollection<VaultSection> TagSections { get; } = [];

    public VaultSection TrashSection { get; }

    public bool HasTags => TagSections.Count > 0;

    /// <summary>
    /// The PC selects the first item when nothing is selected, so the detail column is never empty; a phone shows the
    /// detail in place of the list, so it waits for a tap.
    /// </summary>
    public bool AutoSelectFirst { get; set; }

    /// <summary>The items of the current section that match the search.</summary>
    public ObservableCollection<VaultCardViewModel> Cards { get; } = [];

    public bool IsEmpty => Cards.Count == 0;

    public bool IsTrash => Section.Kind == VaultSectionKind.Trash;

    public bool HasUnreadable => UnreadableCount > 0;

    /// <summary>Nothing matches in this section, but there may be matches elsewhere: offer to search every item.</summary>
    public bool CanSearchEverywhere => IsEmpty && Search.Trim().Length > 0 && Section.Kind is not (VaultSectionKind.All or VaultSectionKind.Trash);

    /// <summary>"Nothing here yet." or "Nothing matches “x” in Cards."</summary>
    public string EmptyText => Search.Trim() is { Length: > 0 } q
        ? $"Nothing matches “{q}”{(Section.Kind == VaultSectionKind.All ? "" : " in " + Section.Label)}."
        : IsTrash ? "The trash is empty." : "Nothing here yet.";

    /// <summary>A new item or a change is being made: the editor pop-up is open over the vault.</summary>
    public bool IsEditorOpen => Editor is not null;

    /// <summary>Moves every shown one-time code on; the app calls it every second while the vault is on screen.</summary>
    public void TickTotp(DateTimeOffset now)
    {
        foreach (var card in Cards)
            if (card.HasTotp) card.Tick(now);
        Detail?.Tick(now);
        Editor?.Tick(now);
    }

    partial void OnUnreadableCountChanged(int value) => OnPropertyChanged(nameof(HasUnreadable));

    partial void OnSectionChanged(VaultSection? oldValue, VaultSection newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        OnPropertyChanged(nameof(IsTrash));
        if (!_quietSection) Refresh();
    }

    /// <summary>Changes the section from inside <see cref="Refresh"/> (no second refresh).</summary>
    private void SetSectionQuietly(VaultSection section)
    {
        _quietSection = true;
        try
        {
            Section = section;
        }
        finally
        {
            _quietSection = false;
        }
    }

    partial void OnSearchChanged(string value) => Refresh();

    partial void OnSelectedCardChanged(VaultCardViewModel? value)
    {
        if (_refreshing) return;
        Detail = value is null ? null : DetailFor(value.Uid);
        _session.Touch();
    }

    partial void OnEditorChanged(ItemDetailViewModel? oldValue, ItemDetailViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnEditorPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnEditorPropertyChanged;
        OnPropertyChanged(nameof(IsEditorOpen));
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Cancel on an existing item ends editing: the pop-up closes.
        if (e.PropertyName == nameof(ItemDetailViewModel.IsEditing) && sender is ItemDetailViewModel { IsEditing: false } editor && ReferenceEquals(editor, Editor))
            Editor = null;
    }

    /// <summary>Rebuilds the list and the counts from the store (after a save here or a sync), keeping the selection.</summary>
    public void Refresh()
    {
        if (_session.State != VaultState.Unlocked)
        {
            Clear();
            return;
        }
        var items = _store.Items();
        var trash = _store.Trash();
        RefreshSections(items, trash);

        var query = Search.Trim();
        var selected = SelectedCard?.Uid;
        _refreshing = true;
        try
        {
            Cards.Clear();
            var entries = IsTrash ? trash : items.Where(e => Section.Contains(e.Item));
            foreach (var entry in entries.Where(e => Matches(e, query)).OrderBy(e => Title(e), StringComparer.CurrentCultureIgnoreCase))
                Cards.Add(new VaultCardViewModel(entry, _platform, _session));
            SelectedCard = selected is null ? null : Cards.FirstOrDefault(c => c.Uid == selected);
        }
        finally
        {
            _refreshing = false;
        }
        UnreadableCount = _store.Unreadable().Count;

        if (SelectedCard is null && AutoSelectFirst) SelectedCard = Cards.FirstOrDefault(); // its detail opens
        else if (SelectedCard is { } card) Detail = DetailFor(card.Uid); // what changed shows (e.g. a sync)
        else Detail = null;
        TickTotp(DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanSearchEverywhere));
    }

    private void RefreshSections(IReadOnlyList<VaultEntry> items, IReadOnlyList<VaultEntry> trash)
    {
        foreach (var section in Sections) section.Count = items.Count(e => section.Contains(e.Item));
        TrashSection.Count = trash.Count;

        // Tags as people typed them; "Work" and "work" are one tag.
        var tags = items.SelectMany(e => e.Item.Tags).Where(t => t.Trim().Length > 0)
            .GroupBy(t => t.Trim(), StringComparer.CurrentCultureIgnoreCase).Select(g => g.First().Trim())
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!tags.SequenceEqual(TagSections.Select(s => s.Tag!), StringComparer.CurrentCultureIgnoreCase))
        {
            TagSections.Clear();
            foreach (var tag in tags) TagSections.Add(new VaultSection(VaultSectionKind.Tag, tag, tag) { IsSelected = Section.IsTag && string.Equals(Section.Tag, tag, StringComparison.CurrentCultureIgnoreCase) });
            OnPropertyChanged(nameof(HasTags));
        }
        foreach (var section in TagSections) section.Count = items.Count(e => section.Contains(e.Item));

        // The tag shown was removed from its last item: back to everything.
        if (Section.IsTag && !TagSections.Any(s => string.Equals(s.Tag, Section.Tag, StringComparison.CurrentCultureIgnoreCase)))
            SetSectionQuietly(Sections[0]);
        else if (Section.IsTag)
        {
            // The rebuilt tag list has a new object for the shown tag.
            var current = TagSections.First(s => string.Equals(s.Tag, Section.Tag, StringComparison.CurrentCultureIgnoreCase));
            if (!ReferenceEquals(current, Section)) SetSectionQuietly(current);
        }
    }

    /// <summary>Drops everything decrypted (the vault locked), the editor too.</summary>
    public void Clear()
    {
        _refreshing = true;
        try
        {
            Cards.Clear();
            SelectedCard = null;
        }
        finally
        {
            _refreshing = false;
        }
        Detail = null;
        Editor = null;
        TagSections.Clear();
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Hides every secret shown with "Show" (the open item and the editor): the page was left or hidden.</summary>
    public void HideRevealed()
    {
        foreach (var card in Cards) card.IsRevealed = false;
        foreach (var detail in new[] { Detail, Editor })
            if (detail is not null)
                foreach (var field in detail.Fields) field.IsRevealed = false;
    }

    [RelayCommand]
    private void ShowSection(VaultSection? section)
    {
        if (section is null) return;
        if (ReferenceEquals(section, Section)) Refresh();
        else Section = section;
        _session.Touch();
    }

    /// <summary>The search found nothing in this section: look in every item.</summary>
    [RelayCommand]
    private void SearchEverywhere() => Section = Sections[0];

    /// <summary>Opens the editor for a new item of the kind.</summary>
    [RelayCommand]
    private void New(VaultItemKind kind)
    {
        Editor = new ItemDetailViewModel(_store, _files, _session, _platform, null, kind, OnEditorSaved);
        _session.Touch();
    }

    /// <summary>Opens the editor on an item (its Edit button). The detail keeps showing the item as it is meanwhile.</summary>
    internal void EditItem(string uid)
    {
        if (_store.Get(uid) is not { Trashed: false } entry) return;
        Editor = new ItemDetailViewModel(_store, _files, _session, _platform, entry, entry.Item.Kind, OnEditorSaved, EditItem) { IsEditing = true };
        _session.Touch();
    }

    /// <summary>Shows an item (a tap on its row; on the PC the list selection does the same).</summary>
    [RelayCommand]
    private void Open(VaultCardViewModel? card)
    {
        if (card is null) return;
        if (ReferenceEquals(card, SelectedCard)) Detail ??= DetailFor(card.Uid);
        else SelectedCard = card;
    }

    /// <summary>Back on a phone: from the item to the list.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        SelectedCard = null;
        Detail = null;
        _session.Touch();
    }

    /// <summary>The star: favorite or not (not a new version of the item).</summary>
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

    /// <summary>The detail's own star.</summary>
    internal void ToggleFavorite(string uid)
    {
        if (Cards.FirstOrDefault(c => c.Uid == uid) is { } card) ToggleFavorite(card);
        else if (_store.Get(uid) is { Trashed: false } entry)
        {
            _store.SetFavorite(uid, !entry.Item.Favorite);
            Refresh();
        }
    }

    /// <summary>The detail of an existing item; null when it is gone (e.g. deleted on another device).</summary>
    private ItemDetailViewModel? DetailFor(string uid) =>
        _store.Get(uid) is { } entry ? new ItemDetailViewModel(_store, _files, _session, _platform, entry, entry.Item.Kind, OnDetailChanged, EditItem, ToggleFavorite) : null;

    /// <summary>The editor saved (uid) or was cancelled (null): it closes, and a saved item is selected where it now is.</summary>
    private void OnEditorSaved(string? uid)
    {
        Editor = null;
        if (uid is not null) ShowItem(uid);
        else Refresh();
    }

    /// <summary>The detail trashed, restored, deleted or settled a conflict: the list follows.</summary>
    private void OnDetailChanged(string? uid)
    {
        if (uid is null)
        {
            CloseDetail();
            Refresh();
        }
        else ShowItem(uid);
    }

    /// <summary>Selects an item, moving to a section that shows it when the current one does not.</summary>
    private void ShowItem(string uid)
    {
        if (_store.Get(uid) is not { } entry)
        {
            Refresh();
            return;
        }
        var wanted = entry.Trashed ? TrashSection : Section.Kind != VaultSectionKind.Trash && Section.Contains(entry.Item) ? Section : Sections[0];
        if (!Matches(entry, Search.Trim())) Search = "";
        _refreshing = true;
        try
        {
            SelectedCard = null;
        }
        finally
        {
            _refreshing = false;
        }
        if (!ReferenceEquals(wanted, Section)) Section = wanted; // refreshes
        else Refresh();
        SelectedCard = Cards.FirstOrDefault(c => c.Uid == uid);
    }

    private static string Title(VaultEntry entry) => entry.Item.Title.Length > 0 ? entry.Item.Title : "Untitled";

    private static bool Matches(VaultEntry entry, string query)
    {
        if (query.Length == 0) return true;
        var item = entry.Item;
        // Search what the list shows anyway (an Info item shows its description): never secret values.
        return item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Attachments.Any(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || (!item.IsLoginOrToken && item.Notes.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || item.Fields.Any(f => !f.IsSecret && !f.IsFile && f.Kind is not VaultFieldKind.Multiline
                && f.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
}
