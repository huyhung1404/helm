using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Text;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WatchLater;

/// <summary>One saved video on the page.</summary>
public sealed partial class WatchRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private string _kindName = "";
    [ObservableProperty] private bool _isShort;
    [ObservableProperty] private bool _watched;
    [ObservableProperty] private WatchSource _source;
    [ObservableProperty] private string? _thumbnailPath;
    [ObservableProperty] private bool _isEditingNote;
    [ObservableProperty] private string _noteDraft = "";
    [ObservableProperty] private double _watchedPercent;

    // Download
    [ObservableProperty] private string _downloadText = "";
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _canCancelDownload;
    [ObservableProperty] private bool _canOpenFile;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private bool _isProgressKnown;

    public WatchRowViewModel(string id) => Id = id;

    public string Id { get; }

    public string Url { get; private set; } = "";

    internal string? ThumbnailUrl { get; private set; }

    public bool HasNote => Note.Length > 0;
    public bool HasDuration => Duration.Length > 0;
    public bool HasThumbnail => ThumbnailPath is not null;
    public bool HasNoThumbnail => ThumbnailPath is null;
    public bool HasDownloadText => DownloadText.Length > 0;
    public string WatchedLabel => Watched ? "Not watched" : "Watched";
    public bool IsNotEditingNote => !IsEditingNote;
    public bool HasProgress => WatchedPercent > 0;

    /// <summary>0 to 1: how much of the thumbnail's red bar is filled.</summary>
    public double WatchedFraction => WatchedPercent / 100;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnDurationChanged(string value) => OnPropertyChanged(nameof(HasDuration));
    partial void OnDownloadTextChanged(string value) => OnPropertyChanged(nameof(HasDownloadText));
    partial void OnWatchedChanged(bool value) => OnPropertyChanged(nameof(WatchedLabel));
    partial void OnIsEditingNoteChanged(bool value) => OnPropertyChanged(nameof(IsNotEditingNote));
    partial void OnWatchedPercentChanged(double value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(WatchedFraction));
    }

    partial void OnThumbnailPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasNoThumbnail));
    }

    internal void Update(WatchItem item, DateTimeOffset now)
    {
        Url = item.Url;
        Title = item.DisplayTitle;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Channel)) parts.Add(item.Channel.Trim());
        parts.Add(WatchLaterFormat.KindName(item.Source, item.Kind));
        parts.Add(item.Watched && item.WatchedAt is { } w ? "watched " + WatchLaterFormat.When(w, now) : "saved " + WatchLaterFormat.When(item.AddedAt, now));
        if (!item.Watched && item.ResumeSeconds is int at && at > 0) parts.Add("stopped at " + WatchLaterFormat.Duration(at));
        Subtitle = string.Join(" · ", parts);
        WatchedPercent = !item.Watched && item.ResumeSeconds is int r && item.DurationSeconds is int length && length > 0
            ? Math.Clamp(r * 100.0 / length, 1, 100)
            : 0;
        Note = item.Title is { Length: > 0 } || item.Note.Length == 0 ? item.Note : "";
        Duration = item.DurationSeconds is int seconds && seconds > 0 ? WatchLaterFormat.Duration(seconds) : "";
        KindName = WatchLaterFormat.KindName(item.Source, item.Kind);
        IsShort = item.Kind == WatchKind.Short;
        Watched = item.Watched;
        Source = item.Source;
        ThumbnailUrl = item.ThumbnailUrl;
        if (!IsEditingNote) NoteDraft = item.Note;
    }
}

/// <summary>A filter button above the list, with its count.</summary>
public sealed partial class WatchFilterTab(WatchFilter filter, string name) : ObservableObject
{
    [ObservableProperty] private string _label = name;
    [ObservableProperty] private bool _isSelected;

    public WatchFilter Filter { get; } = filter;

    public string Name { get; } = name;
}

/// <summary>A quality in the download picker.</summary>
public sealed partial class QualityChoice(QualityOption option) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public QualityOption Option { get; } = option;
    public string Label => Option.Label;
    public string Detail => Option.Detail;
    public bool HasDetail => Option.Detail.Length > 0;
    public bool Available => Option.Available;
}

/// <summary>
/// The Watch Later page, shared by the Windows and Android apps: saving links, the filtered list, watched, notes and
/// downloads (with the quality picked first), and the settings both apps have. All members are used on the UI thread;
/// store, download and thumbnail changes are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class WatchLaterViewModel : ObservableObject
{
    private readonly WatchLaterStore _store;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IProcessLauncher _launcher;
    private readonly IWatchDownloads _downloads;
    private readonly ThumbnailCache _thumbnails;
    private readonly IVideoPlayer? _player;
    private readonly ILogger<WatchLaterViewModel> _logger;
    private readonly Dictionary<string, WatchRowViewModel> _rows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;
    private CancellationTokenSource? _pickerCts;
    private string? _pickerId;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private string _newLink = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string _addPreview = "";
    [ObservableProperty] private bool _canAdd;
    [ObservableProperty] private int _defaultQualityIndex;

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    // The quality picker
    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private bool _isPickerLoading;
    [ObservableProperty] private string _pickerTitle = "";
    [ObservableProperty] private string _pickerNote = "";
    [ObservableProperty] private QualityChoice? _pickerSelected;
    [ObservableProperty] private bool _canInstallFfmpeg;

    public WatchLaterViewModel(
        WatchLaterStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        IProcessLauncher launcher,
        IWatchDownloads downloads,
        ThumbnailCache thumbnails,
        ILogger<WatchLaterViewModel> logger,
        IVideoPlayer? player = null)
    {
        _player = player;
        _store = store;
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _launcher = launcher;
        _downloads = downloads;
        _thumbnails = thumbnails;
        _logger = logger;

        FilterTabs =
        [
            new WatchFilterTab(WatchFilter.ToWatch, "To watch"),
            new WatchFilterTab(WatchFilter.Videos, "Videos"),
            new WatchFilterTab(WatchFilter.Shorts, "Shorts & Reels"),
            new WatchFilterTab(WatchFilter.Watched, "Watched"),
        ];

        _loading = true;
        var s = _settings.Current;
        Filter = Enum.IsDefined(s.Filter) ? s.Filter : WatchFilter.ToWatch;
        SourceIndex = s.SourceFilter switch { WatchSource.YouTube => 1, WatchSource.Facebook => 2, WatchSource.Other => 3, _ => 0 };
        DefaultQualityIndex = Math.Max(0, QualityIds.ToList().IndexOf(s.DefaultQuality));
        _loading = false;
        UpdateAddPreview();

        _store.Changed += (_, _) => ScheduleRefresh();
        _downloads.StatusChanged += (_, id) => _ui.Post(() => UpdateDownload(id));
        Refresh();
    }

    public ObservableCollection<WatchRowViewModel> Items { get; } = [];

    public IReadOnlyList<WatchFilterTab> FilterTabs { get; }

    public WatchFilter Filter { get; private set; }

    public IReadOnlyList<string> SourceNames { get; } = ["All sites", "YouTube", "Facebook", "Other links"];

    /// <summary>The download qualities, in the order of <see cref="QualityNames"/>.</summary>
    public IReadOnlyList<string> QualityIds { get; } = VideoQuality.Presets.Select(p => p.Id).ToList();

    public IReadOnlyList<string> QualityNames { get; } = VideoQuality.Presets.Select(p => p.Label).ToList();

    public ObservableCollection<QualityChoice> PickerOptions { get; } = [];

    /// <summary>True when downloads happen on this device (Windows); false when a PC is asked to download (Android).</summary>
    public bool DownloadsHere => _downloads.DownloadsHere;

    public string DownloadLabel => _downloads.DownloadsHere ? "Download" : "Download on PC";

    public string PickerConfirmLabel => _downloads.DownloadsHere ? "Download" : "Ask my PC";

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasItems => Items.Count > 0;
    public bool IsListEmpty => Items.Count == 0;
    public bool CanConfirmPicker => PickerSelected is { Available: true } && !IsPickerLoading;

    public string EmptyListText
    {
        get
        {
            if (Search.Trim().Length > 0) return $"No saved video matches “{Search.Trim()}”.";
            return Filter switch
            {
                WatchFilter.Watched => "Nothing watched yet. Videos you mark as watched are kept here.",
                WatchFilter.Shorts => "No Shorts or Reels to watch.",
                WatchFilter.Videos => "No videos to watch.",
                _ => "Nothing to watch yet. Share a YouTube or Facebook video to Helm, or paste its link above.",
            };
        }
    }

    /// <summary>For the settings page: how much space the thumbnails use.</summary>
    public string ThumbnailSummary => $"Thumbnails on this device use {WatchLaterFormat.Size(_thumbnails.Size())}. They are fetched again when needed.";

    public string WatchedSummary
    {
        get
        {
            var n = _store.Counts().Watched;
            return n switch
            {
                0 => "No watched videos.",
                1 => "1 watched video. Deleting it removes it from all your devices.",
                _ => $"{n} watched videos. Deleting them removes them from all your devices.",
            };
        }
    }

    public bool HasWatched => _store.Counts().Watched > 0;

    // ---- Adding --------------------------------------------------------------------------------------------------

    partial void OnNewLinkChanged(string value) => UpdateAddPreview();

    partial void OnNewNoteChanged(string value) => UpdateAddPreview();

    private void UpdateAddPreview()
    {
        if (NewLink.Trim().Length == 0)
        {
            AddPreview = "";
            CanAdd = false;
            return;
        }
        if (VideoLink.Find(NewLink) is not { } link)
        {
            AddPreview = "That is not a link.";
            CanAdd = false;
            return;
        }
        AddPreview = _store.Find(link.Key) is { } existing
            ? $"Already saved: “{WatchLaterFormat.Shorten(existing.Value.DisplayTitle, 60)}”. Adding moves it to the top."
            : $"New {WatchLaterFormat.KindName(link.Source, link.Kind)}";
        CanAdd = true;
    }

    [RelayCommand]
    private void Add()
    {
        if (VideoLink.Find(NewLink) is not { } link) return;
        try
        {
            var around = VideoLink.TextAround(NewLink, link);
            var note = string.Join('\n', new[] { around, NewNote.Trim() }.Where(s => s.Length > 0));
            var result = _store.Add(link, note);
            NewLink = "";
            NewNote = "";
            if (Filter == WatchFilter.Watched || (Filter == WatchFilter.Shorts && link.Kind != WatchKind.Short) || (Filter == WatchFilter.Videos && link.Kind != WatchKind.Video))
                SelectFilter(WatchFilter.ToWatch);
            if (Search.Length > 0) Search = "";
            Message = result.Existed ? "That video was saved already: it is at the top again." : null;
            RefreshList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving a video failed");
            Message = $"Could not save the video: {ex.Message}";
        }
    }

    /// <summary>Puts a link from the clipboard or a drop into the box.</summary>
    public void Paste(string text) => NewLink = text.Trim();

    // ---- Filters -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowFilter(WatchFilterTab? tab)
    {
        if (tab is not null) SelectFilter(tab.Filter);
    }

    private void SelectFilter(WatchFilter filter)
    {
        Filter = filter;
        OnPropertyChanged(nameof(Filter));
        if (!_loading) _settings.Update(s => s.Filter = filter);
        RefreshList();
    }

    partial void OnSearchChanged(string value) => RefreshList();

    partial void OnSourceIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _settings.Update(s => s.SourceFilter = SourceFilter);
        RefreshList();
    }

    private WatchSource? SourceFilter => SourceIndex switch { 1 => WatchSource.YouTube, 2 => WatchSource.Facebook, 3 => WatchSource.Other, _ => null };

    // ---- Row actions ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Open(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        try
        {
            // In Helm's own player when it can play it (Windows); else in the browser or the app, where watching stopped.
            if (_player?.CanPlay(item) == true) _player.Play(row.Id);
            else _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(item));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening a video failed");
            Message = $"Could not open the link: {ex.Message}";
        }
    }

    /// <summary>In the browser (Windows) or the YouTube / Facebook app (Android), where watching stopped.</summary>
    [RelayCommand]
    private void OpenOutside(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        try
        {
            _launcher.OpenUrl(WatchLaterFormat.ResumeUrl(item));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening a video failed");
            Message = $"Could not open the link: {ex.Message}";
        }
    }

    /// <summary>True when Play opens Helm's own player (Windows); the card then also offers the browser.</summary>
    public bool HasPlayer => _player is not null;

    [RelayCommand]
    private void ToggleWatched(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        var watched = !item.Watched;
        if (Try(() => _store.SetWatched(row.Id, watched)))
            Message = watched && Filter != WatchFilter.Watched ? $"Marked as watched: “{WatchLaterFormat.Shorten(item.DisplayTitle, 50)}”." : null;
    }

    [RelayCommand]
    private async Task DeleteAsync(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        var ok = await _dialogs.ConfirmAsync($"Delete “{WatchLaterFormat.Shorten(item.DisplayTitle, 60)}”?",
            "It is removed from Watch Later on all your devices. A downloaded file stays where it is.", "Delete").ConfigureAwait(true);
        if (!ok) return;
        _downloads.Forget(row.Id);
        Try(() => _store.Delete(row.Id));
    }

    [RelayCommand]
    private void CopyLink(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        _clipboard.SetText(item.Url);
        Message = "Link copied.";
    }

    [RelayCommand]
    private void EditNote(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        foreach (var other in Items.Where(r => r.IsEditingNote && r != row)) other.IsEditingNote = false;
        row.NoteDraft = item.Note;
        row.IsEditingNote = true;
    }

    [RelayCommand]
    private void SaveNote(WatchRowViewModel? row)
    {
        if (row is null) return;
        if (Try(() => _store.SetNote(row.Id, row.NoteDraft))) row.IsEditingNote = false;
    }

    [RelayCommand]
    private void CancelNote(WatchRowViewModel? row)
    {
        if (row is null) return;
        row.IsEditingNote = false;
        if (_store.Get(row.Id) is { } item) row.NoteDraft = item.Note;
    }

    // ---- Downloads -----------------------------------------------------------------------------------------------

    /// <summary>Opens the quality picker for a video (Windows looks up the qualities it really has first).</summary>
    [RelayCommand]
    private async Task DownloadAsync(WatchRowViewModel? row)
    {
        if (row is null || _store.Get(row.Id) is not { } item) return;
        await OpenPickerAsync(row.Id, item).ConfigureAwait(true);
    }

    private async Task OpenPickerAsync(string id, WatchItem item)
    {
        _pickerCts?.Cancel();
        var cts = _pickerCts = new CancellationTokenSource();
        _pickerId = id;
        CanInstallFfmpeg = false;
        PickerTitle = item.DisplayTitle;
        PickerNote = _downloads.DownloadsHere ? "Looking up the qualities this video has…" : "Your PC downloads the video in this quality the next time it syncs.";
        PickerOptions.Clear();
        PickerSelected = null;
        IsPickerLoading = true;
        IsPickerOpen = true;
        IReadOnlyList<QualityOption> options;
        try
        {
            options = await _downloads.QualitiesAsync(item, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Listing qualities failed: {Message}", ex.Message);
            options = VideoQuality.Presets;
            PickerNote = $"Could not look up the video ({ex.Message}). Pick a quality; the closest one it has is downloaded.";
        }
        if (cts.IsCancellationRequested || _pickerId != id) return;
        IsPickerLoading = false;
        if (options.Count == 0)
        {
            PickerNote = "This video has nothing to download (it may be private or removed).";
            return;
        }
        if (PickerNote.StartsWith("Looking up", StringComparison.Ordinal)) PickerNote = "";
        foreach (var option in options) PickerOptions.Add(new QualityChoice(option));
        var wanted = VideoQuality.Preferred(options, _settings.Current.DefaultQuality);
        PickQuality(PickerOptions.FirstOrDefault(c => c.Option == wanted));
        CanInstallFfmpeg = _downloads.DownloadsHere && _downloads.NeedsFfmpeg && options.Any(o => o.Detail == VideoQuality.NeedsFfmpeg);
        if (CanInstallFfmpeg && PickerNote.Length == 0)
            PickerNote = "YouTube keeps the picture and the sound apart: ffmpeg puts them together. Install it once to download the video.";
    }

    /// <summary>From the picker: installs ffmpeg, then lists the qualities again.</summary>
    [RelayCommand]
    private async Task InstallFfmpegAsync()
    {
        if (_pickerId is not { } id || _store.Get(id) is not { } item) return;
        CanInstallFfmpeg = false;
        IsPickerLoading = true;
        PickerNote = "Downloading ffmpeg (about 130 MB)…";
        var progress = new Progress<double>(p => PickerNote = $"Downloading ffmpeg… {p * 100:0} %");
        try
        {
            await _downloads.InstallFfmpegAsync(progress, _pickerCts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Installing ffmpeg failed");
            IsPickerLoading = false;
            CanInstallFfmpeg = true;
            PickerNote = $"Could not install ffmpeg: {ex.Message}";
            return;
        }
        if (_pickerId == id) await OpenPickerAsync(id, item).ConfigureAwait(true);
    }

    [RelayCommand]
    private void PickQuality(QualityChoice? choice)
    {
        if (choice is null || !choice.Available) return;
        foreach (var c in PickerOptions) c.IsSelected = c == choice;
        PickerSelected = choice;
    }

    [RelayCommand]
    private void ConfirmDownload()
    {
        if (_pickerId is not { } id || PickerSelected is not { Available: true } choice) return;
        ClosePicker();
        try
        {
            _downloads.Start(id, choice.Option.Id);
            if (!_downloads.DownloadsHere) Message = $"Your PC downloads it in {choice.Label} the next time it syncs.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starting a download failed");
            Message = $"Could not start the download: {ex.Message}";
        }
        UpdateDownload(id);
    }

    [RelayCommand]
    private void ClosePicker()
    {
        _pickerCts?.Cancel();
        _pickerCts = null;
        _pickerId = null;
        IsPickerOpen = false;
        IsPickerLoading = false;
        CanInstallFfmpeg = false;
        PickerOptions.Clear();
        PickerSelected = null;
    }

    [RelayCommand]
    private void CancelDownload(WatchRowViewModel? row)
    {
        if (row is null) return;
        _downloads.Cancel(row.Id);
        UpdateDownload(row.Id);
    }

    [RelayCommand]
    private void OpenFile(WatchRowViewModel? row)
    {
        if (row is null || _downloads.FileFor(row.Id) is not { } file) return;
        try
        {
            _launcher.OpenUrl(file);
        }
        catch (Exception ex)
        {
            Message = $"Could not open the file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowFile(WatchRowViewModel? row)
    {
        if (row is null || _downloads.FileFor(row.Id) is not { } file) return;
        _launcher.OpenFolder(Path.GetDirectoryName(file) ?? file);
    }

    partial void OnIsPickerLoadingChanged(bool value) => OnPropertyChanged(nameof(CanConfirmPicker));

    partial void OnPickerSelectedChanged(QualityChoice? value) => OnPropertyChanged(nameof(CanConfirmPicker));

    // ---- Settings ------------------------------------------------------------------------------------------------

    partial void OnDefaultQualityIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < QualityIds.Count) _settings.Update(s => s.DefaultQuality = QualityIds[value]);
    }

    [RelayCommand]
    private async Task DeleteWatchedAsync()
    {
        var n = _store.Counts().Watched;
        if (n == 0) return;
        var ok = await _dialogs.ConfirmAsync("Delete the watched videos?",
            n == 1 ? "1 video is removed from all your devices." : $"{n} videos are removed from all your devices.", "Delete").ConfigureAwait(true);
        if (!ok) return;
        Try(() => { _store.DeleteWatched(); return true; });
    }

    [RelayCommand]
    private void ClearThumbnails()
    {
        var freed = _thumbnails.Clear();
        Message = $"Freed {WatchLaterFormat.Size(freed)}.";
        OnPropertyChanged(nameof(ThumbnailSummary));
        foreach (var row in Items) row.ThumbnailPath = null;
        RefreshList();
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

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

    public void Refresh()
    {
        try
        {
            RefreshList();
            UpdateAddPreview();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch Later refresh failed");
            Message = $"Could not load the saved videos: {ex.Message}";
        }
    }

    private void RefreshList()
    {
        var now = _store.Now;
        var source = _store.Items(Filter, SourceFilter);
        var terms = TextSearch.Terms(Search);
        var shown = terms.Count == 0 ? source
            : source.Where(i => TextSearch.Score(terms, i.Value.DisplayTitle, $"{i.Value.Channel} {i.Value.Note} {i.Value.Url}") > 0).ToList();

        var live = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<WatchRowViewModel>(shown.Count);
        foreach (var (id, item, _) in shown)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new WatchRowViewModel(id);
            row.Update(item, now);
            UpdateDownload(row, item);
            LoadThumbnail(row);
            wanted.Add(row);
        }
        foreach (var stale in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(stale);
        Reconcile(Items, wanted);

        var (toWatch, videos, shorts, watched) = _store.Counts(SourceFilter);
        foreach (var tab in FilterTabs)
        {
            var count = tab.Filter switch { WatchFilter.Videos => videos, WatchFilter.Shorts => shorts, WatchFilter.Watched => watched, _ => toWatch };
            tab.Label = count > 0 ? $"{tab.Name} ({count})" : tab.Name;
            tab.IsSelected = tab.Filter == Filter;
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyListText));
        OnPropertyChanged(nameof(WatchedSummary));
        OnPropertyChanged(nameof(HasWatched));
    }

    private void LoadThumbnail(WatchRowViewModel row)
    {
        var url = row.ThumbnailUrl;
        if (url is null)
        {
            row.ThumbnailPath = null;
            return;
        }
        if (_thumbnails.Cached(url) is { } cached)
        {
            row.ThumbnailPath = cached;
            return;
        }
        row.ThumbnailPath = null;
        _ = _thumbnails.GetAsync(url).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } path)
                _ui.Post(() => { if (row.ThumbnailUrl == url) row.ThumbnailPath = path; });
        }, TaskScheduler.Default);
    }

    private void UpdateDownload(string id)
    {
        if (_rows.TryGetValue(id, out var row) && _store.Get(id) is { } item) UpdateDownload(row, item);
    }

    private void UpdateDownload(WatchRowViewModel row, WatchItem item)
    {
        var text = "";
        bool canDownload = true, canCancel = false, canOpen = false, downloading = false, known = false;
        double percent = 0;
        if (_downloads.DownloadsHere)
        {
            var status = _downloads.Status(row.Id);
            var file = _downloads.FileFor(row.Id);
            switch (status.State)
            {
                case DownloadState.Downloading:
                    downloading = true;
                    canDownload = false;
                    canCancel = true;
                    known = status.Progress is not null;
                    percent = (status.Progress ?? 0) * 100;
                    text = status.Text.Length > 0 ? status.Text : "Downloading…";
                    break;
                case DownloadState.Queued:
                    canDownload = false;
                    canCancel = true;
                    text = "Waiting to download";
                    break;
                case DownloadState.Failed:
                    text = status.Text;
                    break;
                default:
                    if (file is not null)
                    {
                        canOpen = true;
                        canDownload = false;
                        text = "Downloaded · " + Path.GetFileName(file);
                    }
                    else if (item.DownloadRequest is { } asked)
                    {
                        text = $"Your phone asked for a download ({VideoQuality.Label(asked)})";
                        canCancel = true;
                    }
                    break;
            }
        }
        else if (item.DownloadRequest is { } asked)
        {
            text = $"Your PC downloads it ({VideoQuality.Label(asked)}) the next time it syncs";
            canDownload = false;
            canCancel = true;
        }
        else if (item.DownloadedOn is { Length: > 0 } device)
        {
            text = $"Downloaded on {device}";
        }
        row.DownloadText = text;
        row.CanDownload = canDownload;
        row.CanCancelDownload = canCancel;
        row.CanOpenFile = canOpen;
        row.IsDownloading = downloading;
        row.IsProgressKnown = known;
        row.DownloadPercent = percent;
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
            _logger.LogError(ex, "Watch Later action failed");
            Message = $"That did not work: {ex.Message}";
            return false;
        }
    }

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
