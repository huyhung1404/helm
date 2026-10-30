using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Modules.Vocab;

/// <summary>The page's tabs: learning, adding new words, looking words up, their roots.</summary>
public enum VocabTab
{
    Learn,
    Add,
    Words,
    Roots,
}

/// <summary>
/// The Vocabulary page, shared by the Windows and Android apps. It holds the tabs, the guessing card, adding words and
/// the opened word; the list (<see cref="List"/>), the roots (<see cref="Roots"/>), the agent (<see cref="Agent"/>) and
/// the options (<see cref="Settings"/>) are their own view models. All members are used on the UI thread; store changes
/// (local or synced) are coalesced into one refresh through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class VocabViewModel : ObservableObject
{
    /// <summary>What to ask Claude Code for the waiting words (copied by the button next to the count).</summary>
    public const string AgentPrompt = "Phân tích các từ mới trong Helm Vocabulary (vocab_pending, rồi vocab_save_analyses cho các từ đó).";

    private const int RecentCount = 8;

    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly VocabVoice? _voice;
    private int _refreshQueued;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newWord = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private VocabWordDetailViewModel? _detail;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private VocabDay _today = new(0, 0, 0, 0);

    /// <summary>The tab in front: Learn (only the card), Add (new words and what waits), Words (the list, a word's tree), Roots (families).</summary>
    [ObservableProperty] private VocabTab _tab = VocabTab.Learn;

    public VocabViewModel(VocabStore store, VocabQuiz quiz, VocabSettingsViewModel settings, VocabAgentViewModel agent, IUiDispatcher ui,
        IDialogService dialogs, IClipboardService clipboard, VocabVoice? voice = null)
    {
        _store = store;
        _quiz = quiz;
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _voice = voice;
        Settings = settings;
        Agent = agent;
        List = new VocabWordListViewModel(store, quiz, settings, id => Open(id));
        Roots = new VocabRootsViewModel(store, agent, settings.Current.ItemsShown, id => Open(id));
        // Revealing a card shows how the word is built: the same tree as the Words tab, its links open the word there.
        Card = new VocabCardViewModel(store, quiz, (id, word) => Build(id, word, null), voice, () => Settings.CardOptions)
        {
            ShowYouGlish = settings.Current.ShowYouGlish,
        };
        if (voice is not null) voice.Problem += (_, problem) => _ui.Post(() => Message = problem);
        settings.Changed += (_, name) => OnSettingChanged(name);

        _store.Changed += (_, _) => ScheduleRefresh();
        _store.ReviewsChanged += (_, _) => ScheduleRefresh();
        Refresh();
        Card.ShowNext();
    }

    public VocabSettingsViewModel Settings { get; }

    public VocabWordListViewModel List { get; }

    public VocabRootsViewModel Roots { get; }

    public VocabAgentViewModel Agent { get; }

    /// <summary>The guessing card on the Learn tab.</summary>
    public VocabCardViewModel Card { get; }

    /// <summary>The words added last (Add tab), newest first: what was just added and whether the agent got to it.</summary>
    public ObservableCollection<VocabWordRow> RecentWords { get; } = [];

    public bool HasRecentWords => RecentWords.Count > 0;

    public bool IsLearning => Tab == VocabTab.Learn;

    public bool IsAdding => Tab == VocabTab.Add;

    public bool IsWordsTab => Tab == VocabTab.Words;

    public bool IsRootsTab => Tab == VocabTab.Roots;

    /// <summary>"Words (6)" on the tab.</summary>
    public string WordsTabText => $"Words ({Today.Total})";

    /// <summary>Above the card: what is due today, what is new, what is learned.</summary>
    public string LearnSummary => Today.Total == 0 ? ""
        : string.Join(" · ", new[]
        {
            Today.Due > 0 ? $"{Today.Due} due now" : "Nothing due now",
            Today.New > 0 ? $"{Today.New} new" : "",
            $"{Today.Learned} of {Today.Total} learned",
        }.Where(s => s.Length > 0));

    /// <summary>No word is due and none is new: the cards now are extra practice.</summary>
    public bool IsCaughtUp => Today.Total > 0 && Today.Due == 0 && Today.New == 0;

    public bool HasPending => PendingCount > 0;

    public bool HasDetail => Detail is not null;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string PendingText => PendingCount == 1
        ? "1 word waits for an AI agent to break it down."
        : $"{PendingCount} words wait for an AI agent to break them down.";

    partial void OnTodayChanged(VocabDay value)
    {
        OnPropertyChanged(nameof(LearnSummary));
        OnPropertyChanged(nameof(WordsTabText));
        OnPropertyChanged(nameof(IsCaughtUp));
    }

    partial void OnTabChanged(VocabTab value)
    {
        OnPropertyChanged(nameof(IsLearning));
        OnPropertyChanged(nameof(IsAdding));
        OnPropertyChanged(nameof(IsWordsTab));
        OnPropertyChanged(nameof(IsRootsTab));
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
    }

    partial void OnDetailChanged(VocabWordDetailViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    // ---- tabs ----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowLearn() => Tab = VocabTab.Learn;

    [RelayCommand]
    private void ShowAdd() => Tab = VocabTab.Add;

    [RelayCommand]
    private void ShowWords() => Tab = VocabTab.Words;

    [RelayCommand]
    private void ShowRoots() => Tab = VocabTab.Roots;

    // ---- adding --------------------------------------------------------------------------------------------------

    private bool CanAdd() => NewWord.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        try
        {
            var (_, added) = _store.AddWord(NewWord, NewNote);
            // The new word shows at the top of "Recently added", waiting for the agent; the user can add the next one.
            Message = added ? null : $"“{VocabText.Tidy(NewWord)}” is already in your words.";
            NewWord = "";
            NewNote = "";
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void CopyPrompt()
    {
        _clipboard.SetText(AgentPrompt);
        Message = "Copied. Paste it into Claude Code (Helm's MCP server must be connected).";
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- the opened word -----------------------------------------------------------------------------------------

    /// <summary>Opens a word on the Words tab (its own box selected, or the given part's).</summary>
    public void Open(string id, string? partId = null)
    {
        if (_store.GetWord(id) is not { } word) return;
        Detail = Build(id, word, partId);
        Tab = VocabTab.Words;
        List.Language = word.Language;
    }

    /// <summary>The opened word again after a change (an analysis arriving, a sync), on whichever tab is in front.</summary>
    private void Reload(VocabWordDetailViewModel detail) =>
        Detail = _store.GetWord(detail.Id) is { } word ? Build(detail.Id, word, detail.SelectedNode?.Node.PartId) : null;

    private VocabWordDetailViewModel Build(string id, VocabWord word, string? partId) =>
        new(_store, id, word, _quiz.StatsOf(id), Settings.ShowTraditional, w => Open(w), partId, _voice, Settings.ShowYouGlish);

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    [RelayCommand]
    private async Task DeleteWordAsync()
    {
        if (Detail is not { } detail) return;
        if (!await _dialogs.ConfirmAsync("Delete this word?", $"“{detail.Word.Text}” leaves your words. Its parts stay for your other words.", "Delete")) return;
        _store.DeleteWord(detail.Id);
        Detail = null;
    }

    // ---- refreshing ----------------------------------------------------------------------------------------------

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
        var all = _store.Words();
        PendingCount = all.Count(w => !w.Value.IsAnalyzed);
        Today = _quiz.Today();
        RefreshRecent(all);
        List.Refresh();
        Roots.Refresh();
        // The opened word follows its changes (an analysis arriving from the agent, a sync), keeping the box selected.
        if (Detail is { } detail && !detail.IsEditing) Reload(detail);
        if (!Card.HasWord) Card.ShowNext();
    }

    private void RefreshRecent(IReadOnlyList<SyncedItem<VocabWord>> all)
    {
        var stats = _quiz.Stats();
        RecentWords.Clear();
        foreach (var w in all.Take(RecentCount))
        {
            var id = w.Id;
            RecentWords.Add(w.Value.IsAnalyzed
                ? new VocabWordRow(id, w.Value.Text, w.Value.Phonetic, w.Value.Meaning, false,
                    VocabFormat.Level(stats.GetValueOrDefault(id) ?? VocabWordStats.New, _store.Now), new RelayCommand(() => Open(id)))
                {
                    IsChinese = w.Value.Language == VocabLanguage.Chinese,
                    Level = w.Value.Level,
                }
                : new VocabWordRow(id, w.Value.Text, "", "Waiting for analysis…", true, "", new RelayCommand(() => { }),
                    new RelayCommand(() => _store.DeleteWord(id))));
        }
        OnPropertyChanged(nameof(HasRecentWords));
    }

    private void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(VocabSettingsViewModel.ShowTraditional) or nameof(VocabSettingsViewModel.ShowYouGlish):
                Card.ShowYouGlish = Settings.ShowYouGlish;
                if (Detail is { } detail) Reload(detail);
                break;
            case nameof(VocabSettingsViewModel.ItemsShownIndex):
                List.Pager.PageSize = Settings.Current.ItemsShown;
                Roots.Pager.PageSize = Settings.Current.ItemsShown;
                List.Refresh();
                Roots.Refresh();
                break;
            case nameof(VocabSettingsViewModel.CardModeIndex):
                Card.AskAgain();
                break;
            case nameof(VocabSettingsViewModel.AutoAnalyze):
                Agent.Nudge();
                break;
        }
    }
}
