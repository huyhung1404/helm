using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Names;
using Helm.Modules.NovelReader.Speech;
using Helm.Modules.NovelReader.Text;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader's page, shared by any platform: the library of synced novels, the open novel (chapters converted on a
/// background thread, the place reading stopped saved and synced), reading aloud chapter after chapter, the names
/// panel and suggested names, the word popup, and the options. All members are used on the UI thread; store,
/// dictionary and speech events are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class NovelReaderViewModel : ObservableObject, IReadAloudHost
{
    /// <summary>A position is saved this long after the reader stops scrolling…</summary>
    internal static TimeSpan ProgressQuiet { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>…and at most this often, so scrolling does not push a sync every second.</summary>
    internal static TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(15);

    private readonly NovelStore _store;
    private readonly DictionaryLibrary _dictionaries;
    private readonly ISettingsStore<NovelReaderSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IDeviceInfo _device;
    private readonly SpeechCatalog _catalog;
    private readonly ReadAloudController _reader;
    private readonly AudioCache _audio;
    private readonly ILocalVoiceServer? _localVoice;
    private readonly INameScanAgent? _nameAgent;
    private CancellationTokenSource? _scanCancel;
    private bool _entriesQueued;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<NovelReaderViewModel> _logger;
    private readonly Dictionary<string, BookCardViewModel> _cards = new(StringComparer.Ordinal);
    private IReadOnlyList<Chapter> _chapters = [];
    private Converter? _converter;
    private int _loadVersion;
    private int _openVersion;
    private int _currentParagraph;
    private int _currentSentence;
    private ParagraphViewModel? _speakingParagraph;
    private bool _readerOpensChapter;
    private bool _progressDirty;
    private bool _progressScheduled;
    private DateTimeOffset _lastProgressSave = DateTimeOffset.MinValue;
    private CancellationTokenSource? _downloading;
    private bool _loading;
    private bool _loadingVoices;
    private bool _refreshQueued;

    [ObservableProperty] private string? _openBookId;
    [ObservableProperty] private string _bookTitle = "";
    [ObservableProperty] private int _chapterIndex = -1;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _remoteProgressOffer;
    [ObservableProperty] private string _dictionaryStatus = "";
    [ObservableProperty] private bool _needsDictionaries;

    // Options
    [ObservableProperty] private ConvertMode _mode;
    [ObservableProperty] private bool _prioritizeNames;
    [ObservableProperty] private bool _showChinese;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private double _lineSpacing;
    [ObservableProperty] private bool _showNamesPanel;

    // Reading aloud
    [ObservableProperty] private bool _isSpeaking;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoadingVoice;
    [ObservableProperty] private bool _isFollowing = true;
    [ObservableProperty] private string _currentSentenceText = "";
    [ObservableProperty] private string _playerPosition = "";
    [ObservableProperty] private int _playerParagraph;
    [ObservableProperty] private int _playerParagraphMax;
    [ObservableProperty] private int _sleepTimerIndex;
    [ObservableProperty] private string _sleepTimerLabel = "";
    [ObservableProperty] private bool _fallbackToOfflineVoice;
    [ObservableProperty] private int _playerReady = -1;
    [ObservableProperty] private string _readyLabel = "";
    [ObservableProperty] private bool _isDownloadingAudio;
    [ObservableProperty] private string _audioDownloadText = "";
    [ObservableProperty] private double _audioDownloadFraction;
    [ObservableProperty] private string _audioCacheSize = "";
    [ObservableProperty] private string _localVoiceStatus = "";
    [ObservableProperty] private string _localVoiceFolder = "";
    [ObservableProperty] private int _localVoicePort;
    [ObservableProperty] private bool _stopLocalVoiceWhenIdle;
    [ObservableProperty] private bool _useEdgeVoice;
    [ObservableProperty] private SpeechVoice? _selectedVoice;
    [ObservableProperty] private double _speechRate;
    [ObservableProperty] private double _speechPitch;
    [ObservableProperty] private double _speechVolume;
    [ObservableProperty] private double _paragraphPause;
    [ObservableProperty] private bool _continueToNextChapter;

    // Adding a novel
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _addTitle = "";
    [ObservableProperty] private string _addSummary = "";

    // Names panel
    [ObservableProperty] private string _entryFilter = "";
    [ObservableProperty] private bool _isFindingNames;
    [ObservableProperty] private string _suggestionStatus = "";
    [ObservableProperty] private bool _autoScanNames;
    [ObservableProperty] private int _nameScanModeIndex;
    [ObservableProperty] private bool _isAgentScanning;
    [ObservableProperty] private string _nameAgentStatus = "";
    [ObservableProperty] private int _foundNameCount;

    // Dictionaries (settings page)
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string _dictionarySourcesText = "";

    private string? _addPath;
    private string? _addText;
    private int _addChapterCount;

    public NovelReaderViewModel(NovelStore store, DictionaryLibrary dictionaries, ISettingsStoreFactory settings, IUiDispatcher ui,
        IDialogService dialogs, IDeviceInfo device, SpeechCatalog catalog, IAudioOutput audio, AudioCache audioCache, IProcessLauncher launcher,
        IClipboardService clipboard, ILogger<NovelReaderViewModel> logger, TimeProvider? time = null, ILocalVoiceServer? localVoice = null,
        INameScanAgent? nameAgent = null)
    {
        _store = store;
        _dictionaries = dictionaries;
        _settings = settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _device = device;
        _catalog = catalog;
        _audio = audioCache;
        _localVoice = localVoice;
        _nameAgent = nameAgent;
        if (localVoice is not null) localVoice.StatusChanged += (_, _) => _ui.Post(OnLocalVoiceChanged);
        _reader = new ReadAloudController(catalog, audio, time, disk: audioCache)
        {
            VoiceId = () => SelectedVoice?.Id,
            Options = () => new SpeechOptions(SpeechRate, SpeechPitch, SpeechVolume),
            ParagraphPause = () => ParagraphPause,
            ContinueToNextChapter = () => ContinueToNextChapter,
            FallbackToOfflineVoice = () => FallbackToOfflineVoice,
        };
        _reader.Attach(this);
        _reader.PositionChanged += (_, position) => _ui.Post(() => OnReaderPosition(position));
        _reader.StateChanged += (_, _) => _ui.Post(OnReaderState);
        _reader.Notice += (_, text) => _ui.Post(() => Message = text.Length == 0 ? null : text);
        _reader.ReadyChanged += (_, _) => _ui.Post(OnReaderReady);
        _reader.DownloadChanged += (_, _) => _ui.Post(OnDownloadChanged);
        audio.MediaButton += (_, button) => _ui.Post(() => OnMediaButton(button));
        _launcher = launcher;
        _logger = logger;
        Editor = new WordEditorViewModel(store, clipboard, () => _converter, () => OpenBookId) { ReadFrom = ReadFromSegment };

        _loading = true;
        var s = _settings.Current;
        Mode = s.Mode;
        PrioritizeNames = s.PrioritizeNames;
        ShowChinese = s.ShowChinese;
        FontSize = Math.Clamp(s.FontSize, 12, 40);
        LineSpacing = Math.Clamp(s.LineSpacing, 1, 3);
        ShowNamesPanel = s.ShowNamesPanel;
        SpeechRate = Math.Clamp(s.SpeechRate, 0.5, 3);
        SpeechPitch = Math.Clamp(s.SpeechPitch, 0, 2);
        SpeechVolume = Math.Clamp(s.SpeechVolume, 0, 1);
        ParagraphPause = Math.Clamp(s.ParagraphPause, 0, 2);
        ContinueToNextChapter = s.ContinueToNextChapter;
        FallbackToOfflineVoice = s.FallbackToOfflineVoice;
        LocalVoiceFolder = s.LocalVoiceFolder ?? "";
        LocalVoicePort = s.LocalVoicePort;
        StopLocalVoiceWhenIdle = s.StopLocalVoiceWhenIdle;
        UseEdgeVoice = s.UseEdgeVoice;
        AutoScanNames = s.AutoScanNames;
        NameScanModeIndex = (int)s.NameScanMode;
        DictionarySourcesText = FormatSources(s.DictionarySources);
        LoadVoices();
        OnLocalVoiceChanged();
        _loading = false;

        _store.Changed += (_, e) => _ui.Post(() => OnStoreChanged(e));
        _dictionaries.Changed += (_, _) => _ui.Post(OnDictionariesChanged);
        RefreshLibrary();
        RefreshDictionaryStatus();
    }

    public WordEditorViewModel Editor { get; }

    public ObservableCollection<BookCardViewModel> Books { get; } = [];

    public ObservableCollection<ChapterItem> Chapters { get; } = [];

    public ObservableCollection<ParagraphViewModel> Paragraphs { get; } = [];

    public ObservableCollection<EntryRowViewModel> Entries { get; } = [];

    public ObservableCollection<SuggestionRowViewModel> Suggestions { get; } = [];

    public ObservableCollection<SpeechVoice> Voices { get; } = [];

    public ObservableCollection<DictionaryFile> DictionaryFiles { get; } = [];

    public IReadOnlyList<string> ModeNames { get; } = ["VietPhrase", "Hán Việt", "中文"];

    /// <summary>Asks the page to scroll a paragraph (index in <see cref="Paragraphs"/>) to the top.</summary>
    public event EventHandler<int>? ScrollRequested;

    /// <summary>Asks the page to keep the paragraph being read in view, scrolling smoothly (while following).</summary>
    public event EventHandler<int>? FollowRequested;

    public IReadOnlyList<string> SleepTimerNames { get; } = ["No sleep timer", "15 minutes", "30 minutes", "60 minutes", "End of chapter"];

    public bool IsBookOpen => OpenBookId is not null;
    public bool IsLibraryShown => OpenBookId is null;
    public bool HasBooks => Books.Count > 0;
    public bool HasNoBooks => Books.Count == 0;
    public bool HasMessage => Message is not null;
    public bool HasRemoteProgressOffer => RemoteProgressOffer is not null;
    public bool HasOfflineVietnameseVoice => _catalog.OfflineVietnamese is not null;
    public bool HasNoOfflineVietnameseVoice => !HasOfflineVietnameseVoice;
    public bool ShowBackToReading => IsSpeaking && !IsFollowing;
    public bool HasSleepTimer => SleepTimerLabel.Length > 0;
    public bool CanGoBack => ChapterIndex > 0;
    public bool CanGoForward => ChapterIndex >= 0 && ChapterIndex + 1 < Chapters.Count;
    public string ChapterPosition => ChapterIndex < 0 ? "" : $"{ChapterIndex + 1} / {Chapters.Count}";
    public string SpeechRateLabel => SpeechRate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
    public bool HasSuggestions => Suggestions.Count > 0;

    public int ModeIndex
    {
        get => (int)Mode;
        set
        {
            if (value >= 0 && value <= 2) Mode = (ConvertMode)value;
        }
    }

    partial void OnOpenBookIdChanged(string? value)
    {
        OnPropertyChanged(nameof(OpenBookCard));
        OnPropertyChanged(nameof(IsBookOpen));
        OnPropertyChanged(nameof(IsLibraryShown));
        foreach (var card in Books) card.IsOpen = card.Id == value;
    }

    partial void OnChapterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ChapterPosition));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnRemoteProgressOfferChanged(string? value) => OnPropertyChanged(nameof(HasRemoteProgressOffer));

    partial void OnModeChanged(ConvertMode value)
    {
        OnPropertyChanged(nameof(ModeIndex));
        Save(s => s.Mode = value);
        if (!_loading) Reconvert();
    }

    partial void OnPrioritizeNamesChanged(bool value)
    {
        Save(s => s.PrioritizeNames = value);
        if (!_loading) RebuildAndReconvert();
    }

    partial void OnShowChineseChanged(bool value)
    {
        Save(s => s.ShowChinese = value);
        foreach (var p in Paragraphs) p.ShowChinese = value && !p.IsSceneBreak;
    }

    partial void OnFontSizeChanged(double value) => Save(s => s.FontSize = value);
    partial void OnLineSpacingChanged(double value) => Save(s => s.LineSpacing = value);
    partial void OnShowNamesPanelChanged(bool value)
    {
        Save(s => s.ShowNamesPanel = value);
        if (value) RefreshEntries();
    }

    partial void OnSelectedVoiceChanged(SpeechVoice? value)
    {
        // Only a voice the reader picked is remembered: not the stand-in used while the saved one is missing (the
        // phone's voices load after Helm starts), nor the empty choice while the list is filled again.
        if (!_loadingVoices && value is not null) Save(s => s.VoiceId = value.Id);
        OnPropertyChanged(nameof(SupportsPitch));
        if (!_loading) RefreshChapterMarks();
    }
    partial void OnSpeechRateChanged(double value)
    {
        OnPropertyChanged(nameof(SpeechRateLabel));
        Save(s => s.SpeechRate = value);
    }
    partial void OnSpeechPitchChanged(double value) => Save(s => s.SpeechPitch = value);
    partial void OnSpeechVolumeChanged(double value) => Save(s => s.SpeechVolume = value);
    partial void OnParagraphPauseChanged(double value) => Save(s => s.ParagraphPause = value);
    partial void OnContinueToNextChapterChanged(bool value) => Save(s => s.ContinueToNextChapter = value);
    partial void OnFallbackToOfflineVoiceChanged(bool value) => Save(s => s.FallbackToOfflineVoice = value);
    partial void OnIsSpeakingChanged(bool value) => OnPropertyChanged(nameof(ShowBackToReading));
    partial void OnIsFollowingChanged(bool value) => OnPropertyChanged(nameof(ShowBackToReading));
    partial void OnSleepTimerLabelChanged(string value) => OnPropertyChanged(nameof(HasSleepTimer));
    partial void OnEntryFilterChanged(string value) => RefreshEntries();

    // ---- Opening the page ------------------------------------------------------------------------------------------

    /// <summary>
    /// The page was shown: load the dictionaries in the background and, if no novel is open, open the one read most
    /// recently (on any device) where reading stopped.
    /// </summary>
    public async Task ActivateAsync()
    {
        RefreshLibrary();
        RefreshAudioSize();
        if (_dictionaries.HasFiles) _ = LoadDictionariesAsync();
        if (OpenBookId is not null) return;
        var id = _settings.Current.OpenBookId is { } saved && _store.GetBook(saved) is not null ? saved : _store.LastReadBookId();
        if (id is not null) await OpenBookAsync(id);
    }

    /// <summary>The page was left or the tool turned off: save where reading stopped.</summary>
    public void Deactivate() => FlushProgress();

    // ---- Library ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowLibrary()
    {
        FlushProgress();
        StopSpeaking();
        OpenBookId = null;
        BookTitle = "";
        Paragraphs.Clear();
        Chapters.Clear();
        ChapterIndex = -1;
        _chapters = [];
        RemoteProgressOffer = null;
        Save(s => s.OpenBookId = null);
        RefreshLibrary();
    }

    [RelayCommand]
    private async Task OpenBook(BookCardViewModel? card)
    {
        if (card is not null) await OpenBookAsync(card.Id);
    }

    public async Task OpenBookAsync(string id)
    {
        if (_store.GetBook(id) is not { } book) return;
        FlushProgress();
        StopSpeaking();
        Message = null;
        RemoteProgressOffer = null;
        var version = ++_openVersion;
        IsBusy = true;
        BusyText = _store.IsOnDevice(book) ? "Opening…" : "Downloading the novel…";
        BusyProgress = 0;
        try
        {
            var progress = new Progress<double>(p => BusyProgress = p);
            var text = await _store.ReadBookTextAsync(id, progress);
            var chapters = await Task.Run(() => ChapterSplitter.Split(text));
            if (version != _openVersion) return;
            OpenBookId = id;
            BookTitle = book.Title;
            _chapters = chapters;
            Save(s => s.OpenBookId = id);
            _converter = null;
            await EnsureConverterAsync();
            Chapters.Clear();
            for (var i = 0; i < chapters.Count; i++) Chapters.Add(new ChapterItem(i, chapters[i].Title));
            ConvertChapterTitles();
            RefreshChapterMarks();
            var saved = _store.Progress(id);
            var chapter = Math.Clamp(saved?.Chapter ?? 0, 0, Math.Max(0, chapters.Count - 1));
            await LoadChapterAsync(chapter, saved?.Paragraph ?? 0, save: false, sentence: saved?.Sentence ?? 0, markResume: true);
            RefreshEntries();
            Suggestions.Clear();
            OnPropertyChanged(nameof(HasSuggestions));
            SuggestionStatus = "";
            RefreshFoundNames();
            // A novel opened for the first time: its names are looked for in the background.
            if (AutoScanNames && book.NamesScannedAt is null) _ = ScanNamesAsync(automatic: true);
        }
        catch (BlobUnavailableException)
        {
            Message = "This novel is not on this device yet. Turn on Helm Sync in General → Sync to download it.";
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Novel {Id} is damaged", id);
            Message = "This novel's file is damaged and cannot be opened. Add it again from the .txt file.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not open novel {Id}", id);
            Message = "Could not open the novel: " + ex.Message;
        }
        finally
        {
            if (version == _openVersion) IsBusy = false;
        }
    }

    /// <summary>Step 1 of adding a novel: read the file and show what was found, with a title to edit.</summary>
    public async Task PrepareAddAsync(string path)
    {
        Message = null;
        IsBusy = true;
        BusyText = "Reading the file…";
        try
        {
            var text = await Task.Run(() => TextFiles.ReadAllText(path));
            var chapters = await Task.Run(() => ChapterSplitter.Split(text));
            _addPath = path;
            _addText = text;
            _addChapterCount = chapters.Count;
            AddTitle = Path.GetFileNameWithoutExtension(path);
            var first = chapters.Count > 0 ? chapters[0].Title : "";
            AddSummary = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{chapters.Count} chapters, {text.Length:N0} characters. First chapter: {first}");
            IsAdding = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Could not read the file: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 2: store the novel (encrypted, synced) and open it.</summary>
    [RelayCommand]
    private async Task ConfirmAdd()
    {
        if (_addPath is null || _addText is null) return;
        IsBusy = true;
        BusyText = "Adding the novel…";
        try
        {
            var id = await _store.AddBookAsync(AddTitle, Path.GetFileName(_addPath), _addText, _addChapterCount, _device.DeviceName,
                new Progress<double>(p => BusyProgress = p));
            CancelAdd();
            RefreshLibrary();
            IsBusy = false;
            await OpenBookAsync(id);
        }
        catch (BlobTooLargeException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not add a novel");
            Message = "Could not add the novel: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        _addPath = null;
        _addText = null;
        AddTitle = "";
        AddSummary = "";
    }

    [RelayCommand]
    private void StartRename(BookCardViewModel? card)
    {
        if (card is null) return;
        card.TitleDraft = card.Title;
        card.IsRenaming = true;
    }

    [RelayCommand]
    private void SaveRename(BookCardViewModel? card)
    {
        if (card is null) return;
        card.IsRenaming = false;
        if (card.TitleDraft.Trim().Length == 0 || card.TitleDraft == card.Title) return;
        _store.RenameBook(card.Id, card.TitleDraft);
        if (card.Id == OpenBookId) BookTitle = _store.GetBook(card.Id)?.Title ?? BookTitle;
    }

    [RelayCommand]
    private void CancelRename(BookCardViewModel? card)
    {
        if (card is not null) card.IsRenaming = false;
    }

    [RelayCommand]
    private async Task DeleteBook(BookCardViewModel? card)
    {
        if (card is null) return;
        var ok = await _dialogs.ConfirmAsync("Delete this novel?",
            $"“{card.Title}”, its saved names and where you stopped reading are deleted on every device.", "Delete");
        if (!ok) return;
        if (card.Id == OpenBookId) ShowLibrary();
        _store.DeleteBook(card.Id);
        _audio.Clear(card.Id);
        RefreshLibrary();
    }

    // ---- Covers --------------------------------------------------------------------------------------------------

    /// <summary>The open novel's card (its cover shows next to the title while reading).</summary>
    public BookCardViewModel? OpenBookCard => OpenBookId is { } id && _cards.TryGetValue(id, out var card) ? card : null;

    /// <summary>Sets a novel's cover from a picture the page already made small (JPEG).</summary>
    public async Task SetCoverAsync(BookCardViewModel card, byte[] picture)
    {
        try
        {
            await _store.SetCoverAsync(card.Id, picture);
            card.CoverBlobId = null;
            RefreshLibrary();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BlobTooLargeException)
        {
            Message = "Could not set the cover: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveCover(BookCardViewModel? card)
    {
        if (card is null) return;
        _store.RemoveCover(card.Id);
        RefreshLibrary();
    }

    /// <summary>Reads a stored cover in the background (it may download from another device first).</summary>
    private async Task LoadCoverAsync(BookCardViewModel card, string? blobId)
    {
        card.CoverBlobId = blobId;
        if (blobId is null)
        {
            card.Cover = null;
            return;
        }
        var picture = await _store.ReadCoverAsync(card.Id);
        if (card.CoverBlobId == blobId) card.Cover = picture;
    }

    // ---- Chapters --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task NextChapter() => CanGoForward ? LoadChapterAsync(ChapterIndex + 1, 0) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousChapter() => CanGoBack ? LoadChapterAsync(ChapterIndex - 1, 0) : Task.CompletedTask;

    /// <summary>The chapter picker changed.</summary>
    public Task GoToChapterAsync(int index) =>
        index >= 0 && index < _chapters.Count && index != ChapterIndex ? LoadChapterAsync(index, 0) : Task.CompletedTask;

    /// <summary>Converts a chapter off the UI thread, shows it and scrolls to <paramref name="paragraph"/>.</summary>
    public async Task LoadChapterAsync(int index, int paragraph, bool save = true, int sentence = 0, bool markResume = false)
    {
        if (index < 0 || index >= _chapters.Count) return;
        var version = ++_loadVersion;
        var chapter = _chapters[index];
        await EnsureConverterAsync();
        var converter = _converter;
        var mode = Mode;
        var lines = await Task.Run(() =>
        {
            var list = new List<(string Chinese, ConvertedLine? Line, bool Heading)>(chapter.Paragraphs.Count + 1)
            {
                (chapter.Title, converter?.ConvertLine(chapter.Title, mode), true),
            };
            foreach (var p in chapter.Paragraphs) list.Add((p, converter?.ConvertLine(p, mode), false));
            return list;
        });
        if (version != _loadVersion) return;
        Paragraphs.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            var (chinese, line, heading) = lines[i];
            Paragraphs.Add(new ParagraphViewModel(i, chinese, heading)
            {
                Line = line ?? new ConvertedLine(chinese, [new Segment(0, chinese.Length, chinese, SegmentKind.Text, "")]),
                ShowChinese = ShowChinese && !(line?.IsSceneBreak ?? false),
            });
        }
        ChapterIndex = index;
        _currentParagraph = Math.Clamp(paragraph, 0, Math.Max(0, Paragraphs.Count - 1));
        _currentSentence = Math.Max(0, sentence);
        _speakingParagraph = null;
        if (markResume && (_currentParagraph > 0 || _currentSentence > 0) && _currentParagraph < Paragraphs.Count) Paragraphs[_currentParagraph].IsResumePoint = true;
        UpdatePlayerPosition();
        if (!_readerOpensChapter)
        {
            // Another chapter was picked by hand: reading aloud goes on there (or just points there).
            if (_reader.IsActive) _reader.ChapterChanged(_currentParagraph);
            else _reader.SetPosition(new ReadingPosition(index, _currentParagraph, _currentSentence));
        }
        ScrollRequested?.Invoke(this, _currentParagraph);
        if (!_readerOpensChapter && !_reader.IsActive) _reader.Warm(_currentParagraph, _currentSentence);
        UpdateReady();
        if (save)
        {
            _progressDirty = true;
            FlushProgress();
        }
    }

    // ---- Progress --------------------------------------------------------------------------------------------------

    /// <summary>The page reports the paragraph at the top of the view after the reader scrolled.</summary>
    public void ReportVisibleParagraph(int index)
    {
        if (IsSpeaking || index < 0 || index == _currentParagraph) return;
        _currentParagraph = index;
        _currentSentence = 0;
        ClearResumePoint();
        _reader.SetPosition(new ReadingPosition(ChapterIndex, index, 0));
        UpdatePlayerPosition();
        ScheduleProgressSave();
    }

    [RelayCommand]
    private async Task AcceptRemoteProgress()
    {
        RemoteProgressOffer = null;
        if (OpenBookId is { } id && _store.Progress(id) is { } p) await LoadChapterAsync(p.Chapter, p.Paragraph, save: false, sentence: p.Sentence);
    }

    [RelayCommand]
    private void DismissRemoteProgress() => RemoteProgressOffer = null;

    private void ScheduleProgressSave()
    {
        _progressDirty = true;
        if (_progressScheduled) return;
        _progressScheduled = true;
        var wait = ProgressInterval - (_store.Now - _lastProgressSave);
        if (wait < ProgressQuiet) wait = ProgressQuiet;
        _ = SaveProgressLaterAsync(wait);
    }

    private async Task SaveProgressLaterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() =>
            {
                _progressScheduled = false;
                FlushProgress();
            });
        }
    }

    /// <summary>Writes the current position (chapter, paragraph, sentence) now if it changed.</summary>
    public void FlushProgress()
    {
        if (!_progressDirty || OpenBookId is not { } id || ChapterIndex < 0) return;
        _progressDirty = false;
        _lastProgressSave = _store.Now;
        try
        {
            _store.SaveProgress(id, ChapterIndex, _currentParagraph, _device.DeviceName, _currentSentence, Paragraphs.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not save the reading position");
        }
    }

    // ---- Reading aloud ---------------------------------------------------------------------------------------------

    /// <summary>The reading-aloud engine; the page's player bar drives it through the commands below.</summary>
    public ReadAloudController Reader => _reader;

    /// <summary>▶/⏸: plays from where reading stopped (or the top of the page), pauses, resumes.</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (!IsBookOpen) return;
        if (_reader.State == ReadAloudState.Stopped)
        {
            IsFollowing = true;
            _reader.Play(_currentParagraph, _currentSentence);
        }
        else _reader.PlayPause();
    }

    [RelayCommand]
    private void StopReading() => _reader.Stop();

    [RelayCommand]
    private void NextSentence() => _reader.NextSentence();

    [RelayCommand]
    private void PreviousSentence() => _reader.PreviousSentence();

    [RelayCommand]
    private void NextParagraph() => _reader.NextParagraph();

    [RelayCommand]
    private void PreviousParagraph() => _reader.PreviousParagraph();

    /// <summary>The seek bar was released on a paragraph.</summary>
    public void SeekTo(int paragraph)
    {
        if (!IsBookOpen) return;
        IsFollowing = true;
        if (IsSpeaking) _reader.Seek(paragraph);
        else
        {
            _currentParagraph = Math.Clamp(paragraph, 0, Math.Max(0, Paragraphs.Count - 1));
            _currentSentence = 0;
            _reader.SetPosition(new ReadingPosition(ChapterIndex, _currentParagraph, 0));
            UpdatePlayerPosition();
            ScrollRequested?.Invoke(this, _currentParagraph);
            ScheduleProgressSave();
        }
    }

    /// <summary>"Read from here" on a paragraph (its ▶ button or the menu).</summary>
    public void ReadFrom(ParagraphViewModel paragraph, int sentence = 0)
    {
        IsFollowing = true;
        ClearResumePoint();
        _reader.Play(paragraph.Index, sentence);
    }

    /// <summary>Reading from the sentence that holds a segment (a double-click, or "Read from here" in the word popup).</summary>
    public void ReadFromSegment(ParagraphViewModel paragraph, int segment)
    {
        var sentence = paragraph.Sentences.FirstOrDefault(s => s.Contains(segment));
        ReadFrom(paragraph, sentence?.Index ?? 0);
    }

    /// <summary>"Back to reading position": scroll to the sentence being read and follow it again.</summary>
    [RelayCommand]
    private void FollowReading()
    {
        IsFollowing = true;
        if (IsSpeaking) FollowRequested?.Invoke(this, _currentParagraph);
        else ScrollRequested?.Invoke(this, _currentParagraph);
    }

    /// <summary>The page saw the reader scroll by hand while listening: stop following until asked.</summary>
    public void StopFollowing()
    {
        if (IsSpeaking) IsFollowing = false;
    }

    [RelayCommand]
    private void Faster() => SpeechRate = Math.Min(3, Math.Round(SpeechRate + 0.1, 1));

    [RelayCommand]
    private void Slower() => SpeechRate = Math.Max(0.5, Math.Round(SpeechRate - 0.1, 1));

    [RelayCommand]
    private void RefreshVoices()
    {
        _catalog.Refresh();
        LoadVoices();
    }

    [RelayCommand]
    private void AddVoices() => _launcher.OpenUrl("ms-settings:speech");

    public void StopSpeaking() => _reader.Stop();

    partial void OnSleepTimerIndexChanged(int value)
    {
        switch (value)
        {
            case 1: _reader.SetSleepTimer(TimeSpan.FromMinutes(15)); break;
            case 2: _reader.SetSleepTimer(TimeSpan.FromMinutes(30)); break;
            case 3: _reader.SetSleepTimer(TimeSpan.FromMinutes(60)); break;
            case 4: _reader.SetSleepAtChapterEnd(); break;
            default: _reader.SetSleepTimer(null); break;
        }
    }

    // IReadAloudHost: the open chapter, as reading aloud sees it.
    int IReadAloudHost.ChapterCount => _chapters.Count;

    int IReadAloudHost.ParagraphCount => Paragraphs.Count;

    IReadOnlyList<Sentence> IReadAloudHost.SentencesOf(int paragraph) =>
        paragraph >= 0 && paragraph < Paragraphs.Count ? Paragraphs[paragraph].Sentences : [];

    async Task<bool> IReadAloudHost.OpenChapterForReadingAsync(int chapter)
    {
        _readerOpensChapter = true;
        try
        {
            await LoadChapterAsync(chapter, 0);
        }
        finally
        {
            _readerOpensChapter = false;
        }
        return ChapterIndex == chapter;
    }

    (string Title, string Subtitle) IReadAloudHost.NowPlaying =>
        (BookTitle, ChapterIndex >= 0 && ChapterIndex < Chapters.Count ? Chapters[ChapterIndex].Title : "");

    private void OnReaderPosition(ReadingPosition position)
    {
        if (position.Chapter != ChapterIndex) return;
        var speaking = IsSpeaking;
        var paragraph = position.Paragraph < Paragraphs.Count ? Paragraphs[position.Paragraph] : null;
        if (_speakingParagraph is not null && _speakingParagraph != paragraph)
        {
            _speakingParagraph.IsSpeaking = false;
            _speakingParagraph.SpeakingSentence = -1;
        }
        _speakingParagraph = speaking ? paragraph : null;
        if (paragraph is not null && speaking)
        {
            paragraph.IsSpeaking = true;
            paragraph.SpeakingSentence = position.Sentence;
        }
        else if (paragraph is not null)
        {
            paragraph.IsSpeaking = false;
            paragraph.SpeakingSentence = -1;
        }
        if (position.Paragraph < Paragraphs.Count)
        {
            _currentParagraph = position.Paragraph;
            _currentSentence = position.Sentence;
        }
        CurrentSentenceText = _reader.CurrentText;
        UpdatePlayerPosition();
        UpdateReady();
        if (speaking)
        {
            ClearResumePoint();
            ScheduleProgressSave();
            if (IsFollowing) FollowRequested?.Invoke(this, _currentParagraph);
        }
    }

    private void OnReaderState()
    {
        var state = _reader.State;
        IsSpeaking = state != ReadAloudState.Stopped;
        IsPlaying = state is ReadAloudState.Playing or ReadAloudState.Loading;
        IsLoadingVoice = state == ReadAloudState.Loading;
        if (!IsSpeaking)
        {
            if (_speakingParagraph is not null)
            {
                _speakingParagraph.IsSpeaking = false;
                _speakingParagraph.SpeakingSentence = -1;
                _speakingParagraph = null;
            }
            IsFollowing = true;
            if (SleepTimerIndex != 0 && _reader.SleepAt is null && !_reader.SleepAtChapterEnd) SleepTimerIndex = 0;
            FlushProgress();
        }
        else if (state == ReadAloudState.Paused) FlushProgress();
        SleepTimerLabel = _reader.SleepAt is { } at ? "Stops at " + at.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : _reader.SleepAtChapterEnd ? "Stops at the end of the chapter" : "";
    }

    private void OnMediaButton(MediaButton button)
    {
        switch (button)
        {
            // Headsets and the phone (a call, another app playing) send play and pause apart: never the other one.
            case MediaButton.Play:
                if (_reader.State == ReadAloudState.Paused) _reader.Resume();
                else if (_reader.State == ReadAloudState.Stopped) PlayPause();
                break;
            case MediaButton.Pause:
                _reader.Pause();
                break;
            case MediaButton.PlayPause:
                PlayPause();
                break;
            case MediaButton.Next:
                _reader.NextParagraph();
                break;
            case MediaButton.Previous:
                _reader.PreviousParagraph();
                break;
            case MediaButton.Stop:
                _reader.Stop();
                break;
        }
    }

    private void UpdatePlayerPosition()
    {
        PlayerParagraphMax = Math.Max(0, Paragraphs.Count - 1);
        PlayerParagraph = Math.Min(_currentParagraph, PlayerParagraphMax);
        PlayerPosition = ChapterIndex < 0 ? "" : $"Paragraph {_currentParagraph + 1} / {Paragraphs.Count} · Chapter {ChapterIndex + 1} / {_chapters.Count}";
    }

    private void ClearResumePoint()
    {
        foreach (var p in Paragraphs)
            if (p.IsResumePoint) p.IsResumePoint = false;
    }

    private void LoadVoices()
    {
        // The saved choice first: a voice that shows up later (the phone's voices load after Helm starts) is picked
        // again, instead of the stand-in used meanwhile.
        var keep = _settings.Current.VoiceId ?? SelectedVoice?.Id ?? SpeechCatalog.DefaultVoiceId;
        _loadingVoices = true;
        try
        {
            Voices.Clear();
            foreach (var voice in _catalog.Voices) Voices.Add(voice);
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault(v => v.IsVietnamese) ?? Voices.FirstOrDefault();
        }
        finally
        {
            _loadingVoices = false;
        }
        OnPropertyChanged(nameof(HasOfflineVietnameseVoice));
        OnPropertyChanged(nameof(HasNoOfflineVietnameseVoice));
    }

    // ---- Downloaded audio ------------------------------------------------------------------------------------------

    [RelayCommand]
    private void DownloadThisChapter()
    {
        if (ChapterIndex >= 0) _reader.DownloadChapters(ChapterIndex, ChapterIndex);
    }

    [RelayCommand]
    private void DownloadNextChapters()
    {
        if (ChapterIndex >= 0) _reader.DownloadChapters(ChapterIndex, ChapterIndex + 9);
    }

    [RelayCommand]
    private void DownloadWholeNovel()
    {
        if (ChapterIndex >= 0) _reader.DownloadChapters(0, _chapters.Count - 1);
    }

    [RelayCommand]
    private void StopAudioDownload() => _reader.StopDownload();

    [RelayCommand]
    private async Task ClearAudio()
    {
        var ok = await _dialogs.ConfirmAsync("Delete the downloaded audio?",
            $"The sound kept on this device ({NovelFormat.Size(_audio.SizeOf(null))}) is deleted. It downloads again when you listen.", "Delete");
        if (!ok) return;
        _reader.StopDownload();
        _audio.Clear(null);
        RefreshAudioSize();
        RefreshChapterMarks();
        QueueLibraryRefresh();
    }

    [RelayCommand]
    private void OpenAudioFolder()
    {
        Directory.CreateDirectory(_audio.Root);
        _launcher.OpenFolder(_audio.Root);
    }

    // IReadAloudHost: the open novel and any of its chapters.
    string? IReadAloudHost.BookId => OpenBookId;

    async Task<IReadOnlyList<IReadOnlyList<Sentence>>> IReadAloudHost.ChapterSentencesAsync(int chapter, CancellationToken ct)
    {
        var chapters = _chapters;
        if (chapter < 0 || chapter >= chapters.Count) return [];
        var converter = _converter;
        var mode = Mode;
        var source = chapters[chapter];
        return await Task.Run<IReadOnlyList<IReadOnlyList<Sentence>>>(() =>
            source.Paragraphs.Prepend(source.Title).Select(p => SentenceSplitter.Split(converter?.ConvertLine(p, mode))).ToList(), ct);
    }

    private void OnReaderReady()
    {
        _reader.RefreshReady();
        UpdateReady();
    }

    private void UpdateReady()
    {
        PlayerReady = _reader.ReadyThrough;
        var count = Paragraphs.Count;
        ReadyLabel = PlayerReady < 0 || count == 0 ? ""
            : PlayerReady >= count - 1 ? "Chapter audio ready"
            : $"Audio ready to paragraph {PlayerReady + 1} / {count}";
        if (count > 0 && PlayerReady >= count - 1) RefreshChapterMarks();
    }

    private void OnDownloadChanged()
    {
        var download = _reader.Download;
        IsDownloadingAudio = download.Active;
        AudioDownloadFraction = download.Fraction;
        AudioDownloadText = download.Active
            ? download.FirstChapter == download.LastChapter
                ? $"Downloading audio · chapter {download.Chapter + 1} · {download.Fraction:P0}"
                : $"Downloading audio · chapter {download.Chapter + 1} ({download.Chapter - download.FirstChapter + 1} / {download.LastChapter - download.FirstChapter + 1}) · {download.Fraction:P0}"
            : "";
        if (download.Error is { } error) Message = error;
        if (!download.Active)
        {
            RefreshChapterMarks();
            RefreshAudioSize();
            QueueLibraryRefresh();
        }
        else if (download.Done == download.Total) RefreshChapterMarks();
        if (IsBookOpen) OnReaderReady();
    }

    /// <summary>Marks the chapters whose sound is downloaded in full for the voice and speed in use.</summary>
    private void RefreshChapterMarks()
    {
        if (OpenBookId is not { } book || _reader.Profile is not { } profile) return;
        var downloaded = _audio.DownloadedChapters(book, profile);
        foreach (var item in Chapters) item.IsDownloaded = downloaded.Contains(item.Index);
    }

    private void RefreshAudioSize() => AudioCacheSize = NovelFormat.Size(_audio.SizeOf(null));

    // ---- The voice on this PC (VieNeu-TTS) ----------------------------------------------------------------------

    /// <summary>The local voice server is set up on this PC.</summary>
    public bool HasLocalVoice => _localVoice?.IsInstalled == true;

    public bool IsLocalVoiceRunning => _localVoice?.IsRunning == true;

    /// <summary>Pitch is not supported by the voices on this PC.</summary>
    public bool SupportsPitch => SelectedVoice?.SupportsPitch ?? true;

    [RelayCommand]
    private async Task StartLocalVoice()
    {
        if (_localVoice is null) return;
        if (await _localVoice.EnsureRunningAsync(CancellationToken.None))
        {
            // The server now lists its voices (with who is a woman); show them.
            _catalog.Refresh();
            await Task.Delay(800);
            LoadVoices();
        }
        OnLocalVoiceChanged();
    }

    [RelayCommand]
    private void StopLocalVoice()
    {
        _localVoice?.Stop();
        OnLocalVoiceChanged();
    }

    /// <summary>The VieNeu-TTS folder the user picked (it holds .venv and apps\openai_speech.py).</summary>
    public void SetLocalVoiceFolder(string folder)
    {
        _localVoice?.Stop();
        LocalVoiceFolder = folder;
        Save(s => s.LocalVoiceFolder = folder);
        LoadVoices();
        OnLocalVoiceChanged();
    }

    partial void OnLocalVoicePortChanged(int value)
    {
        if (value is < 1024 or > 65535) return;
        Save(s => s.LocalVoicePort = value);
    }

    partial void OnStopLocalVoiceWhenIdleChanged(bool value) => Save(s => s.StopLocalVoiceWhenIdle = value);

    partial void OnUseEdgeVoiceChanged(bool value)
    {
        Save(s => s.UseEdgeVoice = value);
        if (!_loading) LoadVoices();
    }

    private void OnLocalVoiceChanged()
    {
        LocalVoiceStatus = _localVoice is null ? "" : _localVoice.Status;
        OnPropertyChanged(nameof(HasLocalVoice));
        OnPropertyChanged(nameof(IsLocalVoiceRunning));
    }

    // ---- Names panel and suggestions -------------------------------------------------------------------------------

    private void RefreshEntries()
    {
        Entries.Clear();
        if (OpenBookId is not { } id) return;
        var filter = EntryFilter.Trim();
        foreach (var entry in _store.Entries(id).Concat(_store.Entries(null)))
        {
            if (filter.Length > 0 && !entry.Chinese.Contains(filter, StringComparison.Ordinal)
                && !entry.Vietnamese.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Entries.Add(new EntryRowViewModel(entry));
        }
    }

    [RelayCommand]
    private void EditEntry(EntryRowViewModel? row)
    {
        if (row is null) return;
        row.Draft = row.Vietnamese;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void SaveEntry(EntryRowViewModel? row)
    {
        if (row is null) return;
        row.IsEditing = false;
        if (row.Draft.Trim() == row.Vietnamese) return;
        _store.SaveEntry(row.Entry.BookId, row.Entry.Kind, row.Chinese, row.Draft);
    }

    [RelayCommand]
    private void DeleteEntry(EntryRowViewModel? row)
    {
        if (row is not null) _store.RemoveEntry(row.Entry.BookId, row.Chinese);
    }

    /// <summary>"Scan for names": looks through the whole novel again (the names already there stay).</summary>
    [RelayCommand]
    private Task FindNames() => ScanNamesAsync(automatic: false);

    /// <summary>
    /// Looks for the open novel's character names (off the UI thread) and adds the sure ones to its names, marked as
    /// found; the doubtful ones go to Suggestions. With the AI mode Claude decides; without a key, or when Claude cannot
    /// answer, the logic scan does. Never over a name the reader saved, nor one they deleted.
    /// </summary>
    public async Task ScanNamesAsync(bool automatic)
    {
        if (IsFindingNames || OpenBookId is not { } bookId) return;
        await EnsureConverterAsync();
        if (_converter is not { } converter || _chapters.Count == 0 || OpenBookId != bookId) return;
        IsFindingNames = true;
        SuggestionStatus = "Looking for names…";
        var foundBefore = _store.FoundNameCount(bookId);
        try
        {
            var chapters = _chapters;
            var paragraphs = await Task.Run(() => chapters.SelectMany(c => c.Paragraphs.Prepend(c.Title)).ToList());
            bool Known(string word) => converter.EntriesFor(word).Count > 0;
            var scan = await Task.Run(() => NameScanner.Scan(paragraphs, Known, converter.HanVietOf));
            IReadOnlyList<NameFinding> sure = scan.Sure;
            IReadOnlyList<NameFinding> maybe = scan.Maybe;
            string? note = null;
            string? said = null;
            if (NameScanModeIndex == (int)NameScanMode.Ai)
            {
                if (_nameAgent is null)
                {
                    note = "Finding names with AI runs in Helm on your PC; the names it finds sync to this device.";
                }
                else if (_nameAgent.Unavailable() is { } problem)
                {
                    note = problem + " The names were found without AI.";
                }
                else
                {
                    // The AI adds the names itself through Helm's tools: they arrive in the store while it works.
                    var cts = new CancellationTokenSource();
                    _scanCancel = cts;
                    IsAgentScanning = true;
                    SuggestionStatus = $"{_nameAgent.Name} is reading the possible names…";
                    try
                    {
                        var progress = new Progress<string>(p => SuggestionStatus = p);
                        said = await _nameAgent.FindNamesAsync(bookId, BookTitle, progress, cts.Token);
                        sure = [];
                    }
                    catch (OperationCanceledException)
                    {
                        sure = [];
                        note = "Stopped; the names added so far stay.";
                    }
                    catch (NameAgentException ex)
                    {
                        _logger.LogWarning(ex, "The AI name scan failed");
                        note = ex.Message + " The names were found without AI.";
                    }
                    finally
                    {
                        IsAgentScanning = false;
                        _scanCancel = null;
                        cts.Dispose();
                    }
                }
            }
            if (OpenBookId != bookId) return;
            _store.SaveFoundNames(bookId, sure.Select(f => (f.Chinese, f.Vietnamese)));
            // Counted from the store: the AI adds its names itself.
            var added = Math.Max(0, _store.FoundNameCount(bookId) - foundBefore);
            _store.MarkNamesScanned(bookId);
            var ignored = new HashSet<string>(_store.GetBook(bookId)?.IgnoredNames ?? [], StringComparer.Ordinal);
            Suggestions.Clear();
            foreach (var f in maybe.Where(f => !ignored.Contains(f.Chinese) && _store.GetEntry(bookId, EntryKind.Name, f.Chinese) is null))
                Suggestions.Add(new SuggestionRowViewModel(f.Chinese, f.Count, f.Vietnamese));
            SuggestionStatus = (added == 0 ? "No new names found" : added == 1 ? "Added 1 name" : $"Added {added} names")
                + (Suggestions.Count > 0 ? $"; {Suggestions.Count} more to check below." : ".") + (note is null ? "" : " " + note)
                + (string.IsNullOrWhiteSpace(said) ? "" : $" {_nameAgent?.Name}: {said.Trim()}");
            if (automatic && added > 0) Message = $"Found {added} character names in this novel and added them (Names shows them; undo there).";
            else if (automatic && note is not null) Message = note;
        }
        finally
        {
            IsFindingNames = false;
            OnPropertyChanged(nameof(HasSuggestions));
            RefreshFoundNames();
        }
    }

    /// <summary>Removes every name the scan added to the open novel (names the reader edited stay).</summary>
    [RelayCommand]
    private async Task UndoFoundNames()
    {
        if (OpenBookId is not { } id || _store.FoundNameCount(id) == 0) return;
        var ok = await _dialogs.ConfirmAsync("Remove the names found automatically?",
            $"The {_store.FoundNameCount(id)} names the scan added to this novel are removed on every device. Names you added or edited stay.", "Remove");
        if (ok) _store.RemoveFoundNames(id);
    }

    public bool HasFoundNames => FoundNameCount > 0;

    partial void OnFoundNameCountChanged(int value) => OnPropertyChanged(nameof(HasFoundNames));

    private void RefreshFoundNames() => FoundNameCount = OpenBookId is { } id ? _store.FoundNameCount(id) : 0;

    // ---- The AI name scan: an AI on this PC through Helm's MCP tools ----------------------------------------------

    /// <summary>The AI scan can run here (Windows: Claude Code); elsewhere the AI scan is done on the PC.</summary>
    public bool CanScanWithAi => _nameAgent is not null;

    public IReadOnlyList<string> NameScanModeNames { get; } = ["Logic (free, offline)", "AI: Claude Code on this PC (through MCP)"];

    public bool IsAiScan => NameScanModeIndex == (int)NameScanMode.Ai;

    partial void OnAutoScanNamesChanged(bool value) => Save(s => s.AutoScanNames = value);

    partial void OnNameScanModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAiScan));
        if (value is 0 or 1) Save(s => s.NameScanMode = (NameScanMode)value);
        RefreshNameAgentStatus();
    }

    /// <summary>Stops the AI scan (the names it added so far stay).</summary>
    [RelayCommand]
    private void StopNameScan() => _scanCancel?.Cancel();

    /// <summary>Whether the AI can run now, in words, for the settings page.</summary>
    [RelayCommand]
    private void RefreshNameAgentStatus() =>
        NameAgentStatus = _nameAgent is null ? ""
            : _nameAgent.Unavailable() is { } problem ? problem
            : $"Ready: {_nameAgent.Name} reaches Helm's tools.";

    [RelayCommand]
    private void AddSuggestion(SuggestionRowViewModel? row)
    {
        if (row is null || OpenBookId is not { } id || row.Vietnamese.Trim().Length == 0) return;
        _store.SaveEntry(id, EntryKind.Name, row.Chinese, row.Vietnamese);
        Suggestions.Remove(row);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private void DismissSuggestion(SuggestionRowViewModel? row)
    {
        if (row is null) return;
        // Not a name: later scans leave it out.
        if (OpenBookId is { } id) _store.IgnoreName(id, row.Chinese);
        Suggestions.Remove(row);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    /// <summary>This novel's names (or meanings) as a Names.txt-style file.</summary>
    public string ExportEntries(string? bookId, EntryKind kind) => UserLayers.Export(_store.Entries(bookId), kind);

    /// <summary>Writes this novel's (or all novels') names or meanings to a file the user picked.</summary>
    public void ExportEntriesTo(string? bookId, EntryKind kind, string path)
    {
        try
        {
            TextFiles.WriteAllTextAtomic(path, ExportEntries(bookId, kind));
            Message = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Could not export: " + ex.Message;
        }
    }

    /// <summary>Adds the entries of a Names.txt-style file to this novel (or all novels).</summary>
    public int ImportEntries(string? bookId, EntryKind kind, string path)
    {
        try
        {
            var count = _store.ImportEntries(bookId, kind, TextFiles.ReadAllText(path));
            Message = $"Added {count} {(kind == EntryKind.Name ? "names" : "meanings")}.";
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = "Could not import: " + ex.Message;
            return 0;
        }
    }

    // ---- The word popup --------------------------------------------------------------------------------------------

    /// <summary>A word was clicked.</summary>
    public void OpenWord(ParagraphViewModel paragraph, Segment segment)
    {
        if (!segment.IsWord) return;
        Editor.Open(paragraph, segment);
    }

    // ---- Dictionaries ----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task DownloadDictionaries()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        Message = null;
        var cts = new CancellationTokenSource();
        _downloading = cts;
        try
        {
            var progress = new Progress<(string File, double Fraction)>(p =>
                DownloadStatus = p.File.Length > 0 ? $"Downloading {p.File}… {p.Fraction:P0}" : "Loading…");
            await _dictionaries.DownloadAsync(_settings.Current.DictionarySources, progress, cts.Token);
            DownloadStatus = "Loading…";
            await _dictionaries.ReloadAsync();
            DownloadStatus = "Downloaded.";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not download the dictionaries");
            DownloadStatus = "Could not download: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _downloading = null;
            RefreshDictionaryStatus();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloading?.Cancel();

    /// <summary>Copies dictionary files the user picked; returns a sentence for the page.</summary>
    public async Task ImportDictionariesAsync(IEnumerable<string> paths)
    {
        try
        {
            var skipped = await _dictionaries.ImportAsync(paths);
            DownloadStatus = skipped.Count == 0 ? "Imported. Loading…" : $"Not dictionary files: {string.Join(", ", skipped)}";
            await _dictionaries.ReloadAsync();
            if (skipped.Count == 0) DownloadStatus = "Imported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DownloadStatus = "Could not import: " + ex.Message;
        }
        RefreshDictionaryStatus();
    }

    [RelayCommand]
    private void OpenDictionaryFolder()
    {
        Directory.CreateDirectory(_dictionaries.Folder);
        _launcher.OpenFolder(_dictionaries.Folder);
    }

    /// <summary>The "name = address" lines of the download sources (from the settings page).</summary>
    public void ApplyDictionarySources(string text)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq].Trim();
            var url = line[(eq + 1)..].Trim();
            if (name.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _)) sources[name] = url;
        }
        if (sources.Count == 0) sources = new Dictionary<string, string>(NovelReaderSettings.DefaultSources, StringComparer.OrdinalIgnoreCase);
        Save(s => s.DictionarySources = sources);
        DictionarySourcesText = FormatSources(sources);
    }

    [RelayCommand]
    private void ResetDictionarySources() => ApplyDictionarySources("");

    /// <summary>Frees the dictionaries while the tool is off.</summary>
    public void Release()
    {
        StopSpeaking();
        FlushProgress();
        _converter = null;
        _dictionaries.Unload();
    }

    // ---- Messages --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- Internals -------------------------------------------------------------------------------------------------

    private async Task LoadDictionariesAsync()
    {
        try
        {
            await _dictionaries.EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the dictionaries");
            _ui.Post(() => Message = "Could not read the dictionaries: " + ex.Message);
        }
    }

    private async Task EnsureConverterAsync()
    {
        if (_converter is not null && (_dictionaries.IsLoaded || !_dictionaries.HasFiles)) return;
        DictionarySet set;
        try
        {
            set = _dictionaries.HasFiles ? await _dictionaries.EnsureLoadedAsync() : DictionarySet.Empty;
        }
        catch (Exception ex)
        {
            Message = "Could not read the dictionaries: " + ex.Message;
            set = DictionarySet.Empty;
        }
        _converter = BuildConverter(set);
        RefreshDictionaryStatus();
    }

    private Converter BuildConverter(DictionarySet set)
    {
        var layers = OpenBookId is null ? UserLayers.Empty : UserLayers.Build(_store, OpenBookId);
        return new Converter(set, layers.BookNames, layers.UserNames, layers.Phrases, PrioritizeNames);
    }

    private void RebuildAndReconvert()
    {
        if (OpenBookId is null) return;
        _converter = BuildConverter(_dictionaries.Current);
        Reconvert();
    }

    /// <summary>Converts the open chapter again in place (names or options changed), keeping the scroll position.</summary>
    private void Reconvert()
    {
        if (_converter is not { } converter || Paragraphs.Count == 0) return;
        var mode = Mode;
        var items = Paragraphs.ToList();
        var version = _loadVersion;
        _ = Task.Run(() => items.Select(p => converter.ConvertLine(p.Chinese, mode)).ToList()).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _ui.Post(() =>
            {
                if (version != _loadVersion) return;
                for (var i = 0; i < items.Count; i++) items[i].Line = t.Result[i];
            });
        }, TaskScheduler.Default);
        ConvertChapterTitles();
    }

    private void ConvertChapterTitles()
    {
        if (_converter is not { } converter) return;
        var mode = Mode == ConvertMode.Chinese ? ConvertMode.Chinese : ConvertMode.VietPhrase;
        foreach (var item in Chapters)
            if (item.Index < _chapters.Count) item.Title = converter.ConvertLine(_chapters[item.Index].Title, mode).Text;
    }

    private void OnStoreChanged(NovelChangedEventArgs e)
    {
        switch (e.Area)
        {
            case NovelArea.Entries:
                // A scan adds dozens of names at once: convert again once, not once per name.
                if (OpenBookId is not null && !_entriesQueued)
                {
                    _entriesQueued = true;
                    _ui.Post(() =>
                    {
                        _entriesQueued = false;
                        if (OpenBookId is null) return;
                        RebuildAndReconvert();
                        RefreshEntries();
                        RefreshFoundNames();
                    });
                }
                QueueLibraryRefresh();
                break;
            case NovelArea.Progress:
                if (e.Origin == SyncChangeOrigin.Remote && OpenBookId is { } id && e.Ids.Contains(id)) OfferRemoteProgress(id);
                QueueLibraryRefresh();
                break;
            default:
                if (OpenBookId is { } open && _store.GetBook(open) is { } book) BookTitle = book.Title;
                else if (OpenBookId is not null && e.Origin == SyncChangeOrigin.Remote) ShowLibrary();
                QueueLibraryRefresh();
                break;
        }
    }

    /// <summary>Another device read further in the open novel: offer to jump there, never jump by itself.</summary>
    private void OfferRemoteProgress(string id)
    {
        if (_store.Progress(id) is not { } p || p.Device == _device.DeviceName) return;
        var further = p.Chapter > ChapterIndex
            || (p.Chapter == ChapterIndex && (p.Paragraph > _currentParagraph + 1 || (p.Paragraph >= _currentParagraph && p.Paragraph > 0 && p.Sentence > _currentSentence + 2)));
        RemoteProgressOffer = further ? $"Continue from chapter {p.Chapter + 1}, paragraph {p.Paragraph + 1} (read on {p.Device})" : null;
    }

    private void QueueLibraryRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.Post(() =>
        {
            _refreshQueued = false;
            RefreshLibrary();
        });
    }

    private void RefreshLibrary()
    {
        var now = _store.Now;
        var books = _store.Books();
        var ids = new HashSet<string>(books.Select(b => b.Id), StringComparer.Ordinal);
        foreach (var gone in _cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            Books.Remove(_cards[gone]);
            _cards.Remove(gone);
        }
        for (var i = 0; i < books.Count; i++)
        {
            var (id, book, _) = books[i];
            if (!_cards.TryGetValue(id, out var card)) _cards[id] = card = new BookCardViewModel(id);
            var position = Books.IndexOf(card);
            if (position < 0) Books.Insert(Math.Min(i, Books.Count), card);
            else if (position != i) Books.Move(position, Math.Min(i, Books.Count - 1));
            if (!card.IsRenaming) card.Title = book.Title;
            var progress = _store.Progress(id);
            card.Subtitle = progress is { } p
                ? $"Chapter {p.Chapter + 1} / {book.ChapterCount}{(p.ParagraphCount > 0 ? $" · paragraph {p.Paragraph + 1} / {p.ParagraphCount}" : "")} · read {NovelFormat.Ago(p.UpdatedAt, now)}{(p.Device.Length > 0 ? " on " + p.Device : "")}"
                : $"{book.ChapterCount} chapters · added {NovelFormat.Ago(book.AddedAt, now)}";
            card.Status = _store.IsUploading(book) ? "Waiting to upload" : _store.IsOnDevice(book) ? "" : "In the cloud";
            var names = _store.EntryCount(id);
            var audioBytes = _audio.SizeOf(id);
            card.Names = string.Join(" · ", new[]
            {
                names == 0 ? "" : names == 1 ? "1 saved name" : $"{names} saved names",
                audioBytes > 0 ? "audio " + NovelFormat.Size(audioBytes) : "",
            }.Where(s => s.Length > 0));
            card.IsOpen = id == OpenBookId;
            if (card.CoverBlobId != book.Cover?.Id) _ = LoadCoverAsync(card, book.Cover?.Id);
        }
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(HasNoBooks));
    }

    private void OnDictionariesChanged()
    {
        RefreshDictionaryStatus();
        if (OpenBookId is not null && _dictionaries.IsLoaded) RebuildAndReconvert();
    }

    private void RefreshDictionaryStatus()
    {
        DictionaryFiles.Clear();
        foreach (var file in _dictionaries.Files()) DictionaryFiles.Add(file);
        NeedsDictionaries = !_dictionaries.HasFiles;
        var set = _dictionaries.Current;
        DictionaryStatus = NeedsDictionaries
            ? "No dictionaries on this device yet. Download them or import QuickTranslator files (ChinesePhienAmWords, VietPhrase, Names…)."
            : _dictionaries.IsLoaded
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{set.VietPhrase.Count:N0} phrases, {set.Names.Count:N0} names, {set.HanViet.Count:N0} Hán Việt readings, {set.LuatNhan.Count:N0} LuatNhan rules.")
                : $"{DictionaryFiles.Count} files; loaded when you open a novel.";
    }

    private static string FormatSources(IReadOnlyDictionary<string, string> sources) =>
        string.Join("\n", sources.Select(s => $"{s.Key} = {s.Value}"));

    private void Save(Action<NovelReaderSettings> change)
    {
        if (!_loading) _settings.Update(change);
    }
}
