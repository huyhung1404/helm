using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Core.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Scratch;

/// <summary>One thing in the list.</summary>
public sealed partial class ScratchRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _kindName = "";
    [ObservableProperty] private string _textPreview = "";
    [ObservableProperty] private string _fullText = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private byte[]? _thumbnail;
    [ObservableProperty] private bool _isText;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private bool _trashed;
    [ObservableProperty] private bool _canBecomeNote;

    /// <summary>The card the keyboard acts on (PC: Ctrl+C, Delete, Enter).</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Characters of a text a card shows.</summary>
    public const int MaxCardText = 1500;

    public ScratchRowViewModel(string id) => Id = id;

    public string Id { get; }

    public bool HasThumbnail => Thumbnail is { Length: > 0 };
    public bool HasTextPreview => TextPreview.Length > 0;
    public bool HasNoThumbnail => !HasThumbnail;
    public bool HasStatus => Status.Length > 0;
    public bool IsFile => !IsText;
    public bool IsLive => !Trashed;

    /// <summary>A file that is neither a photo, a video nor text: shown with a document icon.</summary>
    public bool IsDocument => !IsText && !IsVideo && !IsImage;

    partial void OnThumbnailChanged(byte[]? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasNoThumbnail));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnTextPreviewChanged(string value) => OnPropertyChanged(nameof(HasTextPreview));

    partial void OnIsTextChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(IsDocument));
    }

    partial void OnIsVideoChanged(bool value) => OnPropertyChanged(nameof(IsDocument));

    partial void OnIsImageChanged(bool value) => OnPropertyChanged(nameof(IsDocument));

    partial void OnTrashedChanged(bool value) => OnPropertyChanged(nameof(IsLive));

    internal void Update(ScratchItem item, DateTimeOffset now, string status)
    {
        // Unused things go to the trash by themselves: the card says so in its last days, the tooltip always.
        var idleUntil = ScratchStore.IdleUntil(item);
        var trashIn = ScratchFormat.TrashIn(idleUntil, now);
        Name = item.DisplayName.Length > 0 ? item.DisplayName : "(empty)";
        KindName = ScratchFormat.KindName(item);
        var size = item.Kind == ScratchKind.Text
            ? item.Size == 1 ? "1 character" : $"{item.Size:N0} characters"
            : ScratchFormat.Size(item.Size);
        var when = item.Trashed && item.TrashedAt is { } at ? $"deleted {ScratchFormat.When(at, now)}" : ScratchFormat.When(item.AddedAt, now);
        Meta = string.Join(" · ", new[] { KindName, size, item.AddedFrom.Length > 0 ? $"from {item.AddedFrom}" : "", when }.Where(p => p.Length > 0))
            + (item.Trashed ? "" : $"\nGoes to the trash {trashIn} unless you use it (open, copy or send it).");
        Status = status;
        TextPreview = item.Kind == ScratchKind.Text ? Preview(item.Text ?? "") : "";
        // A card shows the whole text (very long ones are cut; Copy and the details have all of it).
        var text = item.Text ?? "";
        FullText = item.Kind != ScratchKind.Text ? "" : text.Length <= MaxCardText ? text : text[..MaxCardText].TrimEnd() + "…";
        Footer = item.Kind == ScratchKind.Text ? when : $"{size} · {when}";
        if (!item.Trashed && idleUntil - now < TimeSpan.FromDays(ScratchViewModel.WarnDays)) Footer += $" · trash {trashIn}";
        if (!ReferenceEquals(Thumbnail, item.Thumbnail)) Thumbnail = item.Thumbnail;
        IsText = item.Kind == ScratchKind.Text;
        IsVideo = item.IsVideo;
        IsImage = item.IsImage;
        Trashed = item.Trashed;
        CanBecomeNote = !item.Trashed && item.CanBecomeNote;
    }

    /// <summary>The lines after the first (the first is the row's name), for its row.</summary>
    private static string Preview(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).Skip(1).Take(2).ToList();
        var preview = string.Join("\n", lines);
        return preview.Length <= 300 ? preview : preview[..299] + "…";
    }
}

/// <summary>A filter button above the list, with its count.</summary>
public sealed partial class ScratchFilterTab(ScratchFilter filter, string name) : ObservableObject
{
    [ObservableProperty] private string _label = filter == ScratchFilter.Trash ? "" : name;
    [ObservableProperty] private bool _isSelected;

    public ScratchFilter Filter { get; } = filter;

    /// <summary>The filter's name, for screen readers and tooltips (the trash shows only its icon and count).</summary>
    public string Name { get; } = name;

    /// <summary>The trash is a bin icon with the count next to it.</summary>
    public bool IsTrash => Filter == ScratchFilter.Trash;

    public bool HasLabel => Label.Length > 0;

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(HasLabel));
}

/// <summary>
/// The Scratch page, shared by the Windows and Android apps: the list with its filters and search, adding files and
/// text, one thing's details, and the actions (open, save, copy or share, trash). All members are used on the UI
/// thread; store changes (local, synced, uploads) are coalesced into one refresh through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class ScratchViewModel : ObservableObject
{
    private readonly ScratchStore _store;
    private readonly IScratchPlatform _platform;
    private readonly ScratchImporter _importer;
    private readonly ScratchClipboardRelay _relay;
    private readonly ISettingsStore<ScratchSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ISyncService? _sync;
    private readonly INoteWriter? _notes;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly ILogger<ScratchViewModel> _logger;
    private readonly Dictionary<string, ScratchRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _work = new(1, 1);
    private int _refreshQueued;
    private bool _loading;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ScratchFilter _filter;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressKnown;
    [ObservableProperty] private bool _isTextBoxOpen;
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private ScratchRowViewModel? _selected;
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string? _syncNote;
    [ObservableProperty] private string _storageSummary = "";
    [ObservableProperty] private bool _receiveClipboard;
    [ObservableProperty] private int _itemsShownIndex;

    public ScratchViewModel(
        ScratchStore store,
        ScratchImporter importer,
        ScratchClipboardRelay relay,
        IScratchPlatform platform,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<ScratchViewModel> logger,
        ISyncService? sync = null,
        INoteWriter? notes = null)
    {
        _store = store;
        _importer = importer;
        _relay = relay;
        _platform = platform;
        _settings = settings.Get<ScratchSettings>(ScratchIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _sync = sync;
        _notes = notes;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _logger = logger;

        FilterTabs =
        [
            new ScratchFilterTab(ScratchFilter.All, "All"),
            new ScratchFilterTab(ScratchFilter.Media, "Photos & videos"),
            new ScratchFilterTab(ScratchFilter.Files, "Files"),
            new ScratchFilterTab(ScratchFilter.Text, "Text"),
            new ScratchFilterTab(ScratchFilter.Trash, "Trash"),
        ];
        Pager = new ListPager(RefreshList, _settings.Current.ItemsShown);
        _loading = true;
        ItemsShownIndex = ListPager.IndexOf(_settings.Current.ItemsShown);
        var wanted = _settings.Current.Filter;
        Filter = Enum.IsDefined(wanted) && wanted != ScratchFilter.Trash ? wanted : ScratchFilter.All;
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        _relay.Notice += (_, text) => Message = text;
        _loading = true;
        ReceiveClipboard = _settings.Current.ReceiveClipboard;
        _loading = false;
        if (_sync is not null) _sync.StatusChanged += (_, _) => ScheduleRefresh();
        Refresh();
    }

    /// <summary>The cards say when they go to the trash this many days before.</summary>
    public const int WarnDays = 2;

    /// <summary>The things shown: the filter, narrowed by the search.</summary>
    public ObservableCollection<ScratchRowViewModel> Rows { get; } = [];

    /// <summary>The cards a page at a time (<see cref="ScratchSettings.ItemsShown"/>).</summary>
    public ListPager Pager { get; }

    public IReadOnlyList<ScratchFilterTab> FilterTabs { get; }

    /// <summary>"Copy" on Windows, "Share" on Android.</summary>
    public string SendLabel => _platform.SendLabel;

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasSyncNote => !string.IsNullOrEmpty(SyncNote);
    public bool HasRows => Rows.Count > 0;
    public bool IsListEmpty => Rows.Count == 0;
    public bool IsNotBusy => !IsBusy;
    public bool ShowingTrash => Filter == ScratchFilter.Trash;
    public bool NotShowingTrash => !ShowingTrash;
    public bool HasTrash => _store.Counts()[ScratchFilter.Trash] > 0;
    public bool IsDetailOpen => Selected is not null;
    public bool IsDetailClosed => Selected is null;

    public string EmptyListText
    {
        get
        {
            if (Search.Trim().Length > 0) return $"Nothing here matches “{Search.Trim()}”.";
            return Filter switch
            {
                ScratchFilter.Trash => $"The trash is empty. Things you delete, and things nobody used for {ScratchStore.IdleDays} days, wait here until you delete them from the trash.",
                ScratchFilter.Media => "No photos or videos yet.",
                ScratchFilter.Files => "No files yet.",
                ScratchFilter.Text => "No text yet. Paste a link, an address or a code to have it on your other devices.",
                _ => "Nothing in Scratch yet. Add photos, videos, files or text here, and take them out on your other devices.",
            };
        }
    }

    /// <summary>For the settings page: how many things are in the trash.</summary>
    public string TrashSummary => _store.Counts()[ScratchFilter.Trash] switch
    {
        0 => $"Nothing in the trash. Things nobody used for {ScratchStore.IdleDays} days go there by themselves, and stay until you delete them from it.",
        1 => "1 thing in the trash. Deleting it from the trash removes it from all your devices.",
        var n => $"{n} things in the trash. Deleting them from the trash removes them from all your devices.",
    };

    // ---- Filters and search --------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowFilter(ScratchFilterTab? tab)
    {
        if (tab is not null) Filter = tab.Filter;
    }

    partial void OnFilterChanged(ScratchFilter value)
    {
        if (!_loading && value != ScratchFilter.Trash) _settings.Update(s => s.Filter = value);
        OnPropertyChanged(nameof(ShowingTrash));
        OnPropertyChanged(nameof(NotShowingTrash));
        OnPropertyChanged(nameof(CanReorder));
        RefreshList();
    }

    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(CanReorder));
        RefreshList();
    }

    // ---- Adding --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        IReadOnlyList<ScratchSource> picked;
        try
        {
            picked = await _platform.PickFilesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail("Could not pick files", ex);
            return;
        }
        await AddSourcesAsync(picked).ConfigureAwait(true);
    }

    /// <summary>
    /// Puts files into Scratch one after the other (picked, dropped, pasted or shared), with progress. A file that
    /// fails is reported and the others still go in.
    /// </summary>
    /// <returns>How many were added.</returns>
    public async Task<int> AddSourcesAsync(IReadOnlyList<ScratchSource> sources, CancellationToken ct = default)
    {
        if (sources.Count == 0) return 0;
        await _work.WaitAsync(ct).ConfigureAwait(true);
        ScratchImportResult result;
        try
        {
            IsBusy = true;
            var progress = new Progress<ScratchImportProgress>(p =>
            {
                BusyText = p.Count == 1 ? $"Adding “{p.Name}”…" : $"Adding {p.Index} of {p.Count}: “{p.Name}”…";
                IsProgressKnown = p.Fraction is not null;
                Progress = (p.Fraction ?? 0) * 100;
            });
            result = await _importer.ImportAsync(sources, progress, ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            _work.Release();
        }
        if (result.Added.Count > 0 && Filter == ScratchFilter.Trash) Filter = ScratchFilter.All;
        Message = result.Summary;
        RefreshList();
        return result.Added.Count;
    }

    [RelayCommand]
    private void OpenTextBox()
    {
        NewText = "";
        IsTextBoxOpen = true;
    }

    [RelayCommand]
    private void CancelText()
    {
        IsTextBoxOpen = false;
        NewText = "";
    }

    [RelayCommand]
    private async Task SaveTextAsync()
    {
        if (await AddTextAsync(NewText).ConfigureAwait(true))
        {
            IsTextBoxOpen = false;
            NewText = "";
        }
    }

    /// <summary>Puts a piece of text into Scratch (typed, pasted, dropped or shared).</summary>
    public async Task<bool> AddTextAsync(string text)
    {
        if (text.Trim().Length == 0)
        {
            Message = "There is no text to add.";
            return false;
        }
        await _work.WaitAsync().ConfigureAwait(true);
        try
        {
            await _importer.AddTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail("Could not add the text", ex);
            return false;
        }
        finally
        {
            _work.Release();
        }
        if (Filter is ScratchFilter.Trash or ScratchFilter.Media or ScratchFilter.Files) Filter = ScratchFilter.All;
        Message = "Text added.";
        RefreshList();
        return true;
    }

    // ---- One thing -----------------------------------------------------------------------------------------------

    /// <summary>Shows a thing's details (the larger picture, the whole text, every action).</summary>
    [RelayCommand]
    private void Select(ScratchRowViewModel? row)
    {
        foreach (var other in _selection) other.IsSelected = false;
        _selection.Clear();
        if (row is not null)
        {
            _selection.Add(row);
            row.IsSelected = true;
        }
        Selected = row;
        DetailText = row is not null && _store.Get(row.Id) is { Kind: ScratchKind.Text } item ? item.Text ?? "" : "";
        OnPropertyChanged(nameof(SelectionCount));
    }

    // ---- Several cards (PC: Shift or Ctrl + click, Ctrl+A) -------------------------------------------------------

    private readonly List<ScratchRowViewModel> _selection = [];

    /// <summary>The cards the keyboard acts on, in the order they were picked.</summary>
    public IReadOnlyList<ScratchRowViewModel> SelectedRows => _selection;

    public int SelectionCount => _selection.Count;

    /// <summary>Adds a card to the selection, or takes it out when it is in already.</summary>
    public void ToggleSelect(ScratchRowViewModel row)
    {
        if (_selection.Remove(row))
        {
            row.IsSelected = false;
        }
        else
        {
            _selection.Add(row);
            row.IsSelected = true;
        }
        Selected = _selection.Count > 0 ? _selection[^1] : null;
        DetailText = "";
        OnPropertyChanged(nameof(SelectionCount));
    }

    /// <summary>Every card shown (the filter and the search).</summary>
    public void SelectAll()
    {
        Select(null);
        foreach (var row in Rows) ToggleSelect(row);
    }

    /// <summary>
    /// Ctrl+C on the selection: texts only → one text, a blank line between them; otherwise the files, all at once
    /// (texts among them are left out and said so).
    /// </summary>
    public async Task CopySelectedAsync()
    {
        var rows = InWallOrder(_selection);
        if (rows.Count == 0) return;
        if (rows.Count == 1)
        {
            await CopyCommand.ExecuteAsync(rows[0]).ConfigureAwait(true);
            return;
        }
        var items = rows.Select(r => (Row: r, Item: _store.Get(r.Id))).Where(x => x.Item is not null).ToList();
        if (items.All(x => x.Item!.Kind == ScratchKind.Text))
        {
            _clipboard.SetText(string.Join("\n\n", items.Select(x => x.Item!.Text ?? "")));
            foreach (var (row, _) in items) Used(row.Id);
            Message = $"Copied {items.Count} texts as one.";
            return;
        }
        var files = new List<ScratchLocalFile>();
        foreach (var (row, item) in items.Where(x => x.Item!.Kind == ScratchKind.File))
        {
            if (await ExportAsync(row.Id, item!).ConfigureAwait(true) is not { } file) return;
            files.Add(file);
        }
        try
        {
            await _platform.CopyToClipboardAsync(files, CancellationToken.None).ConfigureAwait(true);
            foreach (var (row, _) in items.Where(x => x.Item!.Kind == ScratchKind.File)) Used(row.Id);
            var skipped = items.Count - files.Count;
            Message = $"Copied {files.Count} files." + (skipped > 0 ? $" {skipped} text{(skipped == 1 ? " was" : "s were")} left out: copy texts on their own." : "");
        }
        catch (Exception ex)
        {
            Fail("Could not copy the files", ex);
        }
    }

    /// <summary>Shift+Delete on the selection: for good, without the trash, after one question.</summary>
    public async Task DeleteSelectedForeverAsync()
    {
        var rows = InWallOrder(_selection);
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? $"“{rows[0].Name}”" : $"{rows.Count} things";
        var ok = await _dialogs.ConfirmAsync($"Delete {what} for good?",
            "It skips the trash: removed from all your devices, and it cannot be restored.", "Delete").ConfigureAwait(true);
        if (!ok) return;
        // Only the trash is ever deleted for good, so each goes there first, then out.
        Try(() => rows.Count(r => (r.Trashed || _store.MoveToTrash(r.Id)) && _store.DeleteForever(r.Id)));
        foreach (var row in rows) DeleteOpenedCopy(row.Id);
        Select(null);
        Message = $"Deleted {what} for good.";
    }

    /// <summary>Delete on the selection: into the trash; in the trash, for good after one question.</summary>
    public async Task DeleteSelectedAsync()
    {
        var rows = InWallOrder(_selection);
        if (rows.Count == 0) return;
        if (rows.Count == 1)
        {
            if (rows[0].Trashed) await DeleteForeverCommand.ExecuteAsync(rows[0]).ConfigureAwait(true);
            else Trash(rows[0]);
            return;
        }
        var trashed = rows.Where(r => r.Trashed).ToList();
        var live = rows.Where(r => !r.Trashed).ToList();
        if (live.Count > 0)
        {
            Try(() => live.Count(r => _store.MoveToTrash(r.Id)));
            Message = $"Moved {live.Count} things to the trash.";
        }
        if (trashed.Count > 0)
        {
            var ok = await _dialogs.ConfirmAsync($"Delete {trashed.Count} things for good?",
                "They are removed from all your devices and cannot be restored.", "Delete").ConfigureAwait(true);
            if (!ok) return;
            Try(() => trashed.Count(r => _store.DeleteForever(r.Id)));
            foreach (var row in trashed) DeleteOpenedCopy(row.Id);
            Message = $"Deleted {trashed.Count} things for good.";
        }
        Select(null);
    }

    private List<ScratchRowViewModel> InWallOrder(IEnumerable<ScratchRowViewModel> rows)
    {
        var picked = rows.ToHashSet();
        return Rows.Where(picked.Contains).ToList();
    }

    [RelayCommand]
    public void CloseDetail() => Select(null);

    partial void OnSelectedChanged(ScratchRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsDetailOpen));
        OnPropertyChanged(nameof(IsDetailClosed));
    }

    /// <summary>Opens a file with its app; a text shows in the details.</summary>
    [RelayCommand]
    private async Task OpenAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        if (item.Kind == ScratchKind.Text)
        {
            Select(row);
            Used(row.Id);
            return;
        }
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            await _platform.OpenAsync(file, CancellationToken.None).ConfigureAwait(true);
            Used(row.Id);
        }
        catch (Exception ex)
        {
            Fail($"Could not open “{item.Name}”", ex);
        }
    }

    [RelayCommand]
    private async Task SaveAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Kind: ScratchKind.File } item) return;
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            if (await _platform.SaveAsync(file, CancellationToken.None).ConfigureAwait(true) is { } where)
            {
                Used(row.Id);
                Message = $"Saved “{item.Name}” to {where}.";
            }
        }
        catch (Exception ex)
        {
            Fail($"Could not save “{item.Name}”", ex);
        }
    }

    /// <summary>Text goes to the clipboard; a file to the clipboard (Windows) or the share sheet (Android).</summary>
    [RelayCommand]
    private async Task SendAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        if (item.Kind == ScratchKind.Text)
        {
            _clipboard.SetText(item.Text ?? "");
            Used(row.Id);
            Message = "Text copied.";
            return;
        }
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            Message = await _platform.SendAsync([file], CancellationToken.None).ConfigureAwait(true);
            Used(row.Id);
        }
        catch (Exception ex)
        {
            Fail($"Could not {SendLabel.ToLowerInvariant()} “{item.Name}”", ex);
        }
    }

    /// <summary>On the clipboard here and on every other device that is syncing now.</summary>
    [RelayCommand]
    private async Task SendToClipboardAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Trashed: false } item) return;
        if (item.Kind == ScratchKind.File && !_store.IsOnThisDevice(item) && await ExportAsync(row.Id, item).ConfigureAwait(true) is null) return;
        try
        {
            await _relay.CopyHereAsync(row.Id, item).ConfigureAwait(true);
            _relay.Send(row.Id);
            Used(row.Id);
            Message = _sync?.Status.State is SyncState.NotConfigured
                ? $"Copied “{row.Name}” here. Turn on sync to have your other devices copy it too."
                : $"Copied “{row.Name}” here and on your other devices that are syncing.";
        }
        catch (Exception ex)
        {
            Fail($"Could not send “{row.Name}” to the clipboard", ex);
        }
    }

    // ---- Send to Notes --------------------------------------------------------------------------------------------

    /// <summary>Notes is part of the app and turned on (Send to Notes is offered).</summary>
    public bool CanSendToNotes => _notes is not null
        && (!_general.Current.EnabledModules.TryGetValue(_notes.ModuleId, out var on) || on);

    /// <summary>
    /// Keeps a text (or a text file) for good: it becomes a new note with all its text, and the card goes to the trash
    /// (restorable, like anything deleted).
    /// </summary>
    [RelayCommand]
    private async Task SendToNotesAsync(ScratchRowViewModel? row)
    {
        if (row is null) return;
        if (await ToNoteAsync(row).ConfigureAwait(true) is { } title)
        {
            if (Selected == row) CloseDetail();
            Message = $"Moved to Notes as “{title}”. The card is in the trash.";
        }
    }

    /// <summary>The selected texts become notes, one each (PC: Ctrl+Shift+N); photos and other files stay.</summary>
    public async Task SendSelectedToNotesAsync()
    {
        var rows = InWallOrder(_selection);
        if (rows.Count <= 1)
        {
            if (rows.Count == 1) await SendToNotesCommand.ExecuteAsync(rows[0]).ConfigureAwait(true);
            return;
        }
        var texts = rows.Where(r => r.CanBecomeNote).ToList();
        var moved = 0;
        foreach (var row in texts)
        {
            if (await ToNoteAsync(row).ConfigureAwait(true) is null) return;
            moved++;
        }
        Select(null);
        var skipped = rows.Count - texts.Count;
        Message = (moved == 1 ? "Moved 1 text to Notes; its card is in the trash." : $"Moved {moved} texts to Notes; their cards are in the trash.")
            + (skipped == 0 ? "" : skipped == 1 ? " 1 thing that is not text stays here." : $" {skipped} things that are not text stay here.");
    }

    /// <returns>The new note's title; null when it did not work (the message says why).</returns>
    private async Task<string?> ToNoteAsync(ScratchRowViewModel row)
    {
        if (!CanSendToNotes)
        {
            Message = _notes is null ? "Notes is not part of this Helm." : "Notes is turned off. Turn it on in Home → Utilities to move texts there.";
            return null;
        }
        if (_store.Get(row.Id) is not { Trashed: false } item) return null;
        if (!item.CanBecomeNote)
        {
            Message = $"“{row.Name}” is not text: only texts and text files can become notes.";
            return null;
        }
        string text;
        if (item.Kind == ScratchKind.Text)
        {
            text = item.Text ?? "";
        }
        else
        {
            if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return null;
            try
            {
                text = await File.ReadAllTextAsync(file.Path).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail($"Could not read “{item.Name}”", ex);
                return null;
            }
        }
        string title;
        try
        {
            title = _notes!.Write(text).Title;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            Fail($"Could not move “{row.Name}” to Notes", ex);
            return null;
        }
        // The note is safe: the card goes (to the trash, where it can still be restored).
        Try(() => _store.MoveToTrash(row.Id));
        return title;
    }

    partial void OnReceiveClipboardChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.ReceiveClipboard = value);
    }

    partial void OnItemsShownIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        var size = ListPager.SizeAt(value);
        _settings.Update(s => s.ItemsShown = size);
        Pager.PageSize = size;
        RefreshList();
    }

    /// <summary>Cards can be dragged to reorder them, except in the trash and while searching (the order would be unclear).</summary>
    public bool CanReorder => !ShowingTrash && Search.Trim().Length == 0;

    /// <summary>A card dropped on another card's place (dragged on PC, held and dragged on Android).</summary>
    public void Move(string id, string targetId)
    {
        if (!CanReorder) return;
        if (Try(() => _store.Move(id, targetId))) RefreshList();
    }

    /// <summary>The card's Copy: text as text, a file onto this device's clipboard.</summary>
    [RelayCommand]
    private async Task CopyAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        if (item.Kind == ScratchKind.Text)
        {
            _clipboard.SetText(item.Text ?? "");
            Used(row.Id);
            Message = "Text copied.";
            return;
        }
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            await _platform.CopyToClipboardAsync([file], CancellationToken.None).ConfigureAwait(true);
            Used(row.Id);
            Message = $"Copied “{item.Name}”. Paste it into a folder, a chat or an email.";
        }
        catch (Exception ex)
        {
            Fail($"Could not copy “{item.Name}”", ex);
        }
    }

    [RelayCommand]
    private void CopyText(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Kind: ScratchKind.Text } item) return;
        _clipboard.SetText(item.Text ?? "");
        Used(row.Id);
        Message = "Text copied.";
    }

    [RelayCommand]
    private void Trash(ScratchRowViewModel? row)
    {
        if (row is null) return;
        if (!Try(() => _store.MoveToTrash(row.Id))) return;
        if (Selected == row) CloseDetail();
        Message = $"Moved “{row.Name}” to the trash.";
    }

    [RelayCommand]
    private void Restore(ScratchRowViewModel? row)
    {
        if (row is null) return;
        if (!Try(() => _store.Restore(row.Id))) return;
        if (Selected == row) CloseDetail();
        Message = $"Restored “{row.Name}”.";
    }

    [RelayCommand]
    private async Task DeleteForeverAsync(ScratchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Trashed: true }) return;
        var ok = await _dialogs.ConfirmAsync($"Delete “{row.Name}” for good?",
            "It is removed from all your devices and cannot be restored.", "Delete").ConfigureAwait(true);
        if (!ok) return;
        if (!Try(() => _store.DeleteForever(row.Id))) return;
        if (Selected == row) CloseDetail();
        DeleteOpenedCopy(row.Id);
        Message = $"Deleted “{row.Name}” for good.";
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var trash = _store.Items(ScratchFilter.Trash);
        if (trash.Count == 0) return;
        var ok = await _dialogs.ConfirmAsync("Empty the trash?",
            trash.Count == 1 ? "1 thing is removed from all your devices and cannot be restored."
                : $"{trash.Count} things are removed from all your devices and cannot be restored.",
            "Empty trash").ConfigureAwait(true);
        if (!ok) return;
        if (Selected is { Trashed: true }) CloseDetail();
        if (!Try(() => _store.EmptyTrash())) return;
        foreach (var (id, _, _) in trash) DeleteOpenedCopy(id);
        Message = "The trash is empty.";
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- Storage -------------------------------------------------------------------------------------------------

    /// <summary>Removes this device's copies of files that are on the server, and the opened copies.</summary>
    [RelayCommand]
    private void FreeUpSpace()
    {
        try
        {
            var freed = _store.FreeUpSpace() + WipeOpenedCopies();
            Message = freed > 0
                ? $"Freed {ScratchFormat.Size(freed)}. Files download again when you open them."
                : "Nothing to free: files that are not uploaded yet stay on this device.";
        }
        catch (Exception ex)
        {
            Fail("Could not free up space", ex);
        }
        RefreshList();
    }

    /// <summary>Deletes the decrypted copies made for opening, saving and sharing (Scratch calls it when it starts).</summary>
    /// <returns>The bytes freed.</returns>
    public long WipeOpenedCopies()
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(_platform.OpenFolder)) return 0;
            foreach (var file in Directory.EnumerateFiles(_platform.OpenFolder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open in another app: it goes next time.
                }
            }
            foreach (var folder in Directory.EnumerateDirectories(_platform.OpenFolder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Could not clear the opened scratch files");
        }
        return freed;
    }

    // ---- Refresh -------------------------------------------------------------------------------------------------

    /// <summary>Coalesces bursts of store changes (a sync can touch hundreds of records) into one UI refresh.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        try
        {
            TrashIdle();
            RefreshList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scratch refresh failed");
            Message = $"Could not load Scratch: {ex.Message}";
        }
    }

    private void RefreshList()
    {
        var now = _store.Now;
        var state = _sync?.Status.State ?? SyncState.NotConfigured;
        var terms = TextSearch.Terms(Search);
        var source = _store.Items(Filter);
        var found = terms.Count == 0 ? source : source.Where(i => TextSearch.Score(terms, i.Value.DisplayName, i.Value.Text) > 0).ToList();
        var shown = Pager.Take(found, (Filter, Search.Trim()));

        var live = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<ScratchRowViewModel>(shown.Count);
        foreach (var (id, item, _) in shown)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new ScratchRowViewModel(id);
            row.Update(item, now, StatusOf(item, state));
            wanted.Add(row);
        }
        foreach (var stale in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(stale);
        Reconcile(Rows, wanted);

        // The details follow the thing (moved to the trash or deleted on another device: they close).
        if (Selected is { } selected && (!_rows.ContainsKey(selected.Id) || _store.Get(selected.Id) is null)) CloseDetail();
        // Cards that left the wall (trashed, deleted, filtered out) leave the selection.
        var gone = _selection.Where(r => !_rows.ContainsKey(r.Id)).ToList();
        foreach (var row in gone)
        {
            _selection.Remove(row);
            row.IsSelected = false;
        }
        if (gone.Count > 0) OnPropertyChanged(nameof(SelectionCount));

        var counts = _store.Counts();
        foreach (var tab in FilterTabs)
            tab.Label = tab.IsTrash
                ? counts[tab.Filter] > 0 ? counts[tab.Filter].ToString(System.Globalization.CultureInfo.CurrentCulture) : ""
                : counts[tab.Filter] > 0 ? $"{tab.Name} ({counts[tab.Filter]})" : tab.Name;
        foreach (var tab in FilterTabs) tab.IsSelected = tab.Filter == Filter;

        SyncNote = state switch
        {
            SyncState.NotConfigured => "Sync is off, so what you add stays on this device. Turn on sync in General → Sync to reach your other devices.",
            SyncState.QuotaExceeded => "Your sync storage is full: new files wait on this device. Empty the trash, or ask for more space.",
            SyncState.Unauthorized => "This device can no longer sync (its access was removed). Connect it again in General → Sync.",
            _ => null,
        };
        StorageSummary = $"Files take {ScratchFormat.Size(SafeBytesOnThisDevice())} on this device. Freeing up space keeps them on your other devices and the server; they download again when you open them.";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyListText));
        OnPropertyChanged(nameof(HasTrash));
        OnPropertyChanged(nameof(TrashSummary));
    }

    /// <summary>
    /// Unused things go to the trash. Only when this device knows the others' uses: sync is off, or it synced within
    /// the hour (a PC back after two weeks away must not trash what the phone used yesterday).
    /// </summary>
    private void TrashIdle()
    {
        var status = _sync?.Status;
        var current = status is null || status.State == SyncState.NotConfigured
            || (status.LastSyncedAt is { } at && _store.Now - at < TimeSpan.FromHours(1));
        if (!current) return;
        var count = _store.TrashIdle();
        if (count > 0)
            Message = count == 1
                ? $"1 thing nobody used for {ScratchStore.IdleDays} days went to the trash. Restore it there if you still need it."
                : $"{count} things nobody used for {ScratchStore.IdleDays} days went to the trash. Restore them there if you still need them.";
    }

    private string StatusOf(ScratchItem item, SyncState state)
    {
        if (item.Kind == ScratchKind.Text) return "";
        if (_store.IsUploading(item))
            return state == SyncState.NotConfigured ? "Only on this device" : "Waiting to upload";
        return _store.IsOnThisDevice(item) ? "" : "In the cloud";
    }

    private long SafeBytesOnThisDevice()
    {
        try
        {
            return _store.BytesOnThisDevice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnSyncNoteChanged(string? value) => OnPropertyChanged(nameof(HasSyncNote));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A decrypted copy in the private open folder (reused while it is complete), downloading what is missing with
    /// progress. Null when it failed; the message says why.
    /// </summary>
    private async Task<ScratchLocalFile?> ExportAsync(string id, ScratchItem item)
    {
        var name = ScratchFormat.SafeFileName(item.Name);
        var folder = Path.Combine(_platform.OpenFolder, id);
        var path = Path.Combine(folder, name);
        var file = new ScratchLocalFile(path, name, item.MediaType);
        if (File.Exists(path) && new FileInfo(path).Length == item.Size) return file;

        await _work.WaitAsync().ConfigureAwait(true);
        IsBusy = true;
        BusyText = _store.IsOnThisDevice(item) ? $"Opening “{name}”…" : $"Downloading “{name}”…";
        Progress = 0;
        IsProgressKnown = true;
        var partial = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            var progress = new Progress<double>(p => Progress = p * 100);
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await _store.ReadAsync(item, stream, progress).ConfigureAwait(true);
            File.Move(partial, path, overwrite: true);
            return file;
        }
        catch (BlobUnavailableException ex)
        {
            Message = ex.Message;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "A file in Scratch failed its integrity check");
            Message = $"“{name}” is damaged and cannot be opened.";
        }
        catch (Exception ex)
        {
            Fail($"Could not get “{name}”", ex);
        }
        finally
        {
            IsBusy = false;
            _work.Release();
            if (_store.IsOnThisDevice(item)) RefreshList();
        }
        try
        {
            File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private void DeleteOpenedCopy(string id)
    {
        try
        {
            var folder = Path.Combine(_platform.OpenFolder, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open in another app: cleared when Scratch starts next time.
        }
    }

    /// <summary>A thing was used: it stays out of the trash for another <see cref="ScratchStore.IdleDays"/> days.</summary>
    private void Used(string id)
    {
        try
        {
            _store.Touch(id);
        }
        catch (Exception ex)
        {
            // Only a date: never worth failing the copy or the open over.
            _logger.LogWarning(ex, "Could not mark a scratch thing as used");
        }
    }

    private void Fail(string what, Exception ex)
    {
        _logger.LogWarning(ex, "{What}", what);
        Message = $"{what}: {ex.Message}";
    }

    private bool Try(Func<bool> action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scratch action failed");
            Message = $"That did not work: {ex.Message}";
            return false;
        }
    }

    private bool Try(Func<int> action) => Try(() => { action(); return true; });

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="wanted"/> with minimal moves, keeping instances.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], wanted[i])) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, wanted[i]);
        }
    }
}
