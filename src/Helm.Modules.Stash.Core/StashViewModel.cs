using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Stash;

/// <summary>One thing in the list.</summary>
public sealed partial class StashRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _kindName = "";
    [ObservableProperty] private string _textPreview = "";
    [ObservableProperty] private byte[]? _thumbnail;
    [ObservableProperty] private bool _isText;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private bool _trashed;

    public StashRowViewModel(string id) => Id = id;

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

    internal void Update(StashItem item, DateTimeOffset now, string status)
    {
        Name = item.DisplayName.Length > 0 ? item.DisplayName : "(empty)";
        KindName = StashFormat.KindName(item);
        var size = item.Kind == StashKind.Text
            ? item.Size == 1 ? "1 character" : $"{item.Size:N0} characters"
            : StashFormat.Size(item.Size);
        var when = item.Trashed && item.TrashedAt is { } at ? $"deleted {StashFormat.When(at, now)}" : StashFormat.When(item.AddedAt, now);
        Meta = string.Join(" · ", new[] { KindName, size, item.AddedFrom.Length > 0 ? $"from {item.AddedFrom}" : "", when }.Where(p => p.Length > 0));
        Status = status;
        TextPreview = item.Kind == StashKind.Text ? Preview(item.Text ?? "") : "";
        if (!ReferenceEquals(Thumbnail, item.Thumbnail)) Thumbnail = item.Thumbnail;
        IsText = item.Kind == StashKind.Text;
        IsVideo = item.IsVideo;
        IsImage = item.IsImage;
        Trashed = item.Trashed;
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
public sealed partial class StashFilterTab(StashFilter filter, string name) : ObservableObject
{
    [ObservableProperty] private string _label = name;
    [ObservableProperty] private bool _isSelected;

    public StashFilter Filter { get; } = filter;

    public string Name { get; } = name;
}

/// <summary>
/// The Stash page, shared by the Windows and Android apps: the list with its filters and search, adding files and
/// text, one thing's details, and the actions (open, save, copy or share, trash). All members are used on the UI
/// thread; store changes (local, synced, uploads) are coalesced into one refresh through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class StashViewModel : ObservableObject
{
    private readonly StashStore _store;
    private readonly IStashPlatform _platform;
    private readonly StashImporter _importer;
    private readonly StashClipboardRelay _relay;
    private readonly ISettingsStore<StashSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ISyncService? _sync;
    private readonly ILogger<StashViewModel> _logger;
    private readonly Dictionary<string, StashRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _work = new(1, 1);
    private int _refreshQueued;
    private bool _loading;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private StashFilter _filter;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressKnown;
    [ObservableProperty] private bool _isTextBoxOpen;
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private StashRowViewModel? _selected;
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string? _syncNote;
    [ObservableProperty] private string _storageSummary = "";
    [ObservableProperty] private bool _receiveClipboard;

    public StashViewModel(
        StashStore store,
        StashImporter importer,
        StashClipboardRelay relay,
        IStashPlatform platform,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<StashViewModel> logger,
        ISyncService? sync = null)
    {
        _store = store;
        _importer = importer;
        _relay = relay;
        _platform = platform;
        _settings = settings.Get<StashSettings>(StashIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _sync = sync;
        _logger = logger;

        FilterTabs =
        [
            new StashFilterTab(StashFilter.All, "All"),
            new StashFilterTab(StashFilter.Media, "Photos & videos"),
            new StashFilterTab(StashFilter.Files, "Files"),
            new StashFilterTab(StashFilter.Text, "Text"),
            new StashFilterTab(StashFilter.Trash, "Trash"),
        ];
        _loading = true;
        var wanted = _settings.Current.Filter;
        Filter = Enum.IsDefined(wanted) && wanted != StashFilter.Trash ? wanted : StashFilter.All;
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        _relay.Notice += (_, text) => Message = text;
        _loading = true;
        ReceiveClipboard = _settings.Current.ReceiveClipboard;
        _loading = false;
        if (_sync is not null) _sync.StatusChanged += (_, _) => ScheduleRefresh();
        Refresh();
    }

    /// <summary>The things shown: the filter, narrowed by the search.</summary>
    public ObservableCollection<StashRowViewModel> Rows { get; } = [];

    public IReadOnlyList<StashFilterTab> FilterTabs { get; }

    /// <summary>"Copy" on Windows, "Share" on Android.</summary>
    public string SendLabel => _platform.SendLabel;

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasSyncNote => !string.IsNullOrEmpty(SyncNote);
    public bool HasRows => Rows.Count > 0;
    public bool IsListEmpty => Rows.Count == 0;
    public bool IsNotBusy => !IsBusy;
    public bool ShowingTrash => Filter == StashFilter.Trash;
    public bool NotShowingTrash => !ShowingTrash;
    public bool HasTrash => _store.Counts()[StashFilter.Trash] > 0;
    public bool IsDetailOpen => Selected is not null;
    public bool IsDetailClosed => Selected is null;

    public string EmptyListText
    {
        get
        {
            if (Search.Trim().Length > 0) return $"Nothing here matches “{Search.Trim()}”.";
            return Filter switch
            {
                StashFilter.Trash => "The trash is empty. Things you delete wait here until you delete them from the trash.",
                StashFilter.Media => "No photos or videos yet.",
                StashFilter.Files => "No files yet.",
                StashFilter.Text => "No text yet. Paste a link, an address or a code to have it on your other devices.",
                _ => "Nothing in the stash yet. Add photos, videos, files or text here, and take them out on your other devices.",
            };
        }
    }

    /// <summary>For the settings page: how many things are in the trash.</summary>
    public string TrashSummary => _store.Counts()[StashFilter.Trash] switch
    {
        0 => "Nothing in the trash. Things stay there until you delete them from it.",
        1 => "1 thing in the trash. Deleting it from the trash removes it from all your devices.",
        var n => $"{n} things in the trash. Deleting them from the trash removes them from all your devices.",
    };

    // ---- Filters and search --------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowFilter(StashFilterTab? tab)
    {
        if (tab is not null) Filter = tab.Filter;
    }

    partial void OnFilterChanged(StashFilter value)
    {
        if (!_loading && value != StashFilter.Trash) _settings.Update(s => s.Filter = value);
        OnPropertyChanged(nameof(ShowingTrash));
        OnPropertyChanged(nameof(NotShowingTrash));
        RefreshList();
    }

    partial void OnSearchChanged(string value) => RefreshList();

    // ---- Adding --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        IReadOnlyList<StashSource> picked;
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
    /// Puts files into the stash one after the other (picked, dropped, pasted or shared), with progress. A file that
    /// fails is reported and the others still go in.
    /// </summary>
    /// <returns>How many were added.</returns>
    public async Task<int> AddSourcesAsync(IReadOnlyList<StashSource> sources, CancellationToken ct = default)
    {
        if (sources.Count == 0) return 0;
        await _work.WaitAsync(ct).ConfigureAwait(true);
        StashImportResult result;
        try
        {
            IsBusy = true;
            var progress = new Progress<StashImportProgress>(p =>
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
        if (result.Added.Count > 0 && Filter == StashFilter.Trash) Filter = StashFilter.All;
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

    /// <summary>Puts a piece of text into the stash (typed, pasted, dropped or shared).</summary>
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
        if (Filter is StashFilter.Trash or StashFilter.Media or StashFilter.Files) Filter = StashFilter.All;
        Message = "Text added.";
        RefreshList();
        return true;
    }

    // ---- One thing -----------------------------------------------------------------------------------------------

    /// <summary>Shows a thing's details (the larger picture, the whole text, every action).</summary>
    [RelayCommand]
    private void Select(StashRowViewModel? row)
    {
        Selected = row;
        DetailText = row is not null && _store.Get(row.Id) is { Kind: StashKind.Text } item ? item.Text ?? "" : "";
    }

    [RelayCommand]
    public void CloseDetail() => Select(null);

    partial void OnSelectedChanged(StashRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsDetailOpen));
        OnPropertyChanged(nameof(IsDetailClosed));
    }

    /// <summary>Opens a file with its app; a text shows in the details.</summary>
    [RelayCommand]
    private async Task OpenAsync(StashRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        if (item.Kind == StashKind.Text)
        {
            Select(row);
            return;
        }
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            await _platform.OpenAsync(file, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail($"Could not open “{item.Name}”", ex);
        }
    }

    [RelayCommand]
    private async Task SaveAsync(StashRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Kind: StashKind.File } item) return;
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            if (await _platform.SaveAsync(file, CancellationToken.None).ConfigureAwait(true) is { } where)
                Message = $"Saved “{item.Name}” to {where}.";
        }
        catch (Exception ex)
        {
            Fail($"Could not save “{item.Name}”", ex);
        }
    }

    /// <summary>Text goes to the clipboard; a file to the clipboard (Windows) or the share sheet (Android).</summary>
    [RelayCommand]
    private async Task SendAsync(StashRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        if (item.Kind == StashKind.Text)
        {
            _clipboard.SetText(item.Text ?? "");
            Message = "Text copied.";
            return;
        }
        if (await ExportAsync(row.Id, item).ConfigureAwait(true) is not { } file) return;
        try
        {
            Message = await _platform.SendAsync([file], CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail($"Could not {SendLabel.ToLowerInvariant()} “{item.Name}”", ex);
        }
    }

    /// <summary>On the clipboard here and on every other device that is syncing now.</summary>
    [RelayCommand]
    private async Task SendToClipboardAsync(StashRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Trashed: false } item) return;
        if (item.Kind == StashKind.File && !_store.IsOnThisDevice(item) && await ExportAsync(row.Id, item).ConfigureAwait(true) is null) return;
        try
        {
            await _relay.CopyHereAsync(row.Id, item).ConfigureAwait(true);
            _relay.Send(row.Id);
            Message = _sync?.Status.State is SyncState.NotConfigured
                ? $"Copied “{row.Name}” here. Turn on sync to have your other devices copy it too."
                : $"Copied “{row.Name}” here and on your other devices that are syncing.";
        }
        catch (Exception ex)
        {
            Fail($"Could not send “{row.Name}” to the clipboard", ex);
        }
    }

    partial void OnReceiveClipboardChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.ReceiveClipboard = value);
    }

    [RelayCommand]
    private void CopyText(StashRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { Kind: StashKind.Text } item) return;
        _clipboard.SetText(item.Text ?? "");
        Message = "Text copied.";
    }

    [RelayCommand]
    private void Trash(StashRowViewModel? row)
    {
        if (row is null) return;
        if (!Try(() => _store.MoveToTrash(row.Id))) return;
        if (Selected == row) CloseDetail();
        Message = $"Moved “{row.Name}” to the trash.";
    }

    [RelayCommand]
    private void Restore(StashRowViewModel? row)
    {
        if (row is null) return;
        if (!Try(() => _store.Restore(row.Id))) return;
        if (Selected == row) CloseDetail();
        Message = $"Restored “{row.Name}”.";
    }

    [RelayCommand]
    private async Task DeleteForeverAsync(StashRowViewModel? row)
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
        var trash = _store.Items(StashFilter.Trash);
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
                ? $"Freed {StashFormat.Size(freed)}. Files download again when you open them."
                : "Nothing to free: files that are not uploaded yet stay on this device.";
        }
        catch (Exception ex)
        {
            Fail("Could not free up space", ex);
        }
        RefreshList();
    }

    /// <summary>Deletes the decrypted copies made for opening, saving and sharing (Stash calls it when it starts).</summary>
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
            _logger.LogInformation(ex, "Could not clear the opened stash files");
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
            RefreshList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stash refresh failed");
            Message = $"Could not load the stash: {ex.Message}";
        }
    }

    private void RefreshList()
    {
        var now = _store.Now;
        var state = _sync?.Status.State ?? SyncState.NotConfigured;
        var terms = TextSearch.Terms(Search);
        var source = _store.Items(Filter);
        var shown = terms.Count == 0 ? source : source.Where(i => TextSearch.Score(terms, i.Value.DisplayName, i.Value.Text) > 0).ToList();

        var live = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<StashRowViewModel>(shown.Count);
        foreach (var (id, item, _) in shown)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new StashRowViewModel(id);
            row.Update(item, now, StatusOf(item, state));
            wanted.Add(row);
        }
        foreach (var stale in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(stale);
        Reconcile(Rows, wanted);

        // The details follow the thing (moved to the trash or deleted on another device: they close).
        if (Selected is { } selected && (!_rows.ContainsKey(selected.Id) || _store.Get(selected.Id) is null)) CloseDetail();

        var counts = _store.Counts();
        foreach (var tab in FilterTabs)
            tab.Label = counts[tab.Filter] > 0 ? $"{tab.Name} ({counts[tab.Filter]})" : tab.Name;
        foreach (var tab in FilterTabs) tab.IsSelected = tab.Filter == Filter;

        SyncNote = state switch
        {
            SyncState.NotConfigured => "Sync is off, so what you add stays on this device. Turn on sync in General → Sync to reach your other devices.",
            SyncState.QuotaExceeded => "Your sync storage is full: new files wait on this device. Empty the trash, or ask for more space.",
            SyncState.Unauthorized => "This device can no longer sync (its access was removed). Connect it again in General → Sync.",
            _ => null,
        };
        StorageSummary = $"Files take {StashFormat.Size(SafeBytesOnThisDevice())} on this device. Freeing up space keeps them on your other devices and the server; they download again when you open them.";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyListText));
        OnPropertyChanged(nameof(HasTrash));
        OnPropertyChanged(nameof(TrashSummary));
    }

    private string StatusOf(StashItem item, SyncState state)
    {
        if (item.Kind == StashKind.Text) return "";
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
    private async Task<StashLocalFile?> ExportAsync(string id, StashItem item)
    {
        var name = StashFormat.SafeFileName(item.Name);
        var folder = Path.Combine(_platform.OpenFolder, id);
        var path = Path.Combine(folder, name);
        var file = new StashLocalFile(path, name, item.MediaType);
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
            _logger.LogError(ex, "A stashed file failed its integrity check");
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
            // Open in another app: cleared when Stash starts next time.
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
            _logger.LogError(ex, "Stash action failed");
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
