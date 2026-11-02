using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab;

/// <summary>One line of a word list. A word still waiting for the agent opens nothing; it can be removed (a typo).</summary>
public sealed record VocabWordRow(string Id, string Text, string Phonetic, string Meaning, bool IsPending, string LevelText, IRelayCommand Open,
    IRelayCommand? Remove = null)
{
    /// <summary>The group this row starts ("A", "Due now", "Level 2"); empty when it continues the one above.</summary>
    public string Header { get; init; } = "";

    /// <summary>CEFR / HSK level, shown as a small badge.</summary>
    public string Level { get; init; } = "";

    public bool IsChinese { get; init; }

    /// <summary>Left out of the cards (the user hid it).</summary>
    public bool IsHidden { get; init; }

    /// <summary>The user keeps forgetting it.</summary>
    public bool IsHard { get; init; }

    /// <summary>Chinese: its pinyin keeps slipping while the meaning is known.</summary>
    public bool IsSlipping { get; init; }

    /// <summary>Never answered yet.</summary>
    public bool IsNew { get; init; }

    /// <summary>How well it is known: its quiz level, 0 to <see cref="VocabQuiz.TopLevel"/> (the row's dots).</summary>
    public int Strength { get; init; }

    /// <summary>One flag per level, true up to <see cref="Strength"/>: the dots drawn on the row.</summary>
    public IReadOnlyList<bool> Dots => [.. Enumerable.Range(0, VocabQuiz.TopLevel).Select(i => i < Strength)];

    /// <summary>The one label worth showing next to the word: Hidden, Often forgotten, Pinyin slips or New; empty for none.</summary>
    public string Flag => IsHidden ? "Hidden" : IsHard ? "Often forgotten" : IsSlipping ? "Pinyin slips" : IsNew ? "New" : "";

    public bool HasFlag => Flag.Length > 0;

    public bool HasPhonetic => Phonetic.Length > 0;

    public bool CanRemove => Remove is not null;

    public bool HasHeader => Header.Length > 0;

    public bool HasLevel => Level.Length > 0;
}

/// <summary>A choice of the Words tab's filter: everything, due, new, often forgotten, pinyin slips, hidden, a level, a topic or a tag.</summary>
public sealed record VocabWordFilter(string Name, Func<VocabWord, VocabWordStats, bool> Matches);

/// <summary>A filter as a chip above the list: its name, how many words it would show (with the search), whether it is on.</summary>
public sealed record VocabFilterChip(string Name, int Count, bool IsOn, IRelayCommand Pick);

/// <summary>
/// The Words tab's list: the analyzed words of one language, searched, filtered (level, topic, tag) and sorted (A–Z,
/// newest, weakest, due soonest), with a header where each group starts. UI thread only.
/// </summary>
public sealed partial class VocabWordListViewModel : ObservableObject
{
    public static IReadOnlyList<string> SortNames { get; } = ["A–Z", "Newest first", "Weakest first", "Due soonest"];

    private const string AllWords = "All words";

    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly VocabSettingsViewModel _settings;
    private readonly Action<string> _open;
    private bool _rebuildingFilters;
    // Every word the list shows (all pages), in its order: an opened word's Previous and Next.
    private List<string> _order = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private VocabLanguage _language = VocabLanguage.English;
    [ObservableProperty] private int _englishCount;
    [ObservableProperty] private int _chineseCount;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private IReadOnlyList<string> _filterNames = [AllWords];

    private IReadOnlyList<VocabWordFilter> _filters = [new(AllWords, (_, _) => true)];

    public VocabWordListViewModel(VocabStore store, VocabQuiz quiz, VocabSettingsViewModel settings, Action<string> open)
    {
        _store = store;
        _quiz = quiz;
        _settings = settings;
        _open = open;
        _sortIndex = Math.Clamp(settings.Current.WordsSort, 0, SortNames.Count - 1);
        Pages = new ListPages(Refresh, settings.PageSize);
    }

    public ObservableCollection<VocabWordRow> Words { get; } = [];

    /// <summary>The filters as chips with their counts (the same choices as <see cref="FilterNames"/>).</summary>
    public ObservableCollection<VocabFilterChip> Chips { get; } = [];

    /// <summary>One page of the list at a time (Previous / Next under it).</summary>
    public ListPages Pages { get; }

    public IReadOnlyList<string> Sorts => SortNames;

    public int Count => EnglishCount + ChineseCount;

    public bool HasWords => Count > 0;

    public bool HasNoWords => Count == 0;

    /// <summary>Words exist, but the search or filter leaves none.</summary>
    public bool HasNoMatches => HasWords && Words.Count == 0;

    public bool IsEnglish => Language == VocabLanguage.English;

    public bool IsChinese => Language == VocabLanguage.Chinese;

    public string EnglishTabText => $"English ({EnglishCount})";

    public string ChineseTabText => $"中文 ({ChineseCount})";

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnLanguageChanged(VocabLanguage value)
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsChinese));
        FilterIndex = 0;
        Refresh();
    }

    partial void OnEnglishCountChanged(int value) => Counted();

    partial void OnChineseCountChanged(int value) => Counted();

    partial void OnSortIndexChanged(int value)
    {
        _settings.SaveWordsSort(value);
        Refresh();
    }

    partial void OnFilterIndexChanged(int value)
    {
        if (!_rebuildingFilters) Refresh();
    }

    private void Counted()
    {
        OnPropertyChanged(nameof(EnglishTabText));
        OnPropertyChanged(nameof(ChineseTabText));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasWords));
        OnPropertyChanged(nameof(HasNoWords));
    }

    [RelayCommand]
    private void ShowEnglish() => Language = VocabLanguage.English;

    [RelayCommand]
    private void ShowChinese() => Language = VocabLanguage.Chinese;

    /// <summary>Lists the words again (after any change of data, search, filter, sort or language).</summary>
    public void Refresh()
    {
        var analyzed = _store.Words().Where(w => w.Value.IsAnalyzed).ToList();
        EnglishCount = analyzed.Count(w => w.Value.Language == VocabLanguage.English);
        ChineseCount = analyzed.Count - EnglishCount;
        var mine = analyzed.Where(w => w.Value.Language == Language).ToList();
        var stats = _quiz.Stats();
        var now = _store.Now;
        VocabWordStats Of(string id) => stats.GetValueOrDefault(id) ?? VocabWordStats.New;
        RebuildFilters(mine.Select(w => (w.Value, Of(w.Id))).ToList());

        var terms = TextSearch.Terms(SearchText);
        var filter = _filters[Math.Clamp(FilterIndex, 0, _filters.Count - 1)];
        var searched = mine
            .Where(w => terms.Count == 0 || TextSearch.Score(terms, w.Value.Text, $"{w.Value.Meaning} {w.Value.HanViet} {w.Value.Note}") > 0)
            .ToList();
        RefreshChips(searched.Select(w => (w.Value, Of(w.Id))).ToList());
        var matches = searched.Where(w => filter.Matches(w.Value, Of(w.Id)));
        var sorted = (SortIndex switch
        {
            1 => matches.OrderByDescending(w => w.Value.CreatedAt),
            2 => matches.OrderBy(w => Of(w.Id).IsNew ? -1 : Of(w.Id).Level).ThenByDescending(w => Of(w.Id).Forgot),
            3 => matches.OrderBy(w => Of(w.Id).Due ?? DateTimeOffset.MinValue),
            _ => matches.OrderBy(w => SortKey(w.Value), StringComparer.Ordinal),
        }).ThenBy(w => w.Value.Text, StringComparer.Ordinal).ToList();

        _order = sorted.Select(w => w.Id).ToList();
        Words.Clear();
        var last = "";
        foreach (var w in Pages.Take(sorted, (SearchText, Language, SortIndex, FilterIndex)))
        {
            var s = Of(w.Id);
            var group = Group(w.Value, s, now);
            Words.Add(new VocabWordRow(w.Id, w.Value.Text, w.Value.Phonetic, w.Value.Meaning, false, VocabFormat.Level(s, now),
                new RelayCommand(() => _open(w.Id)))
            {
                Header = group == last ? "" : group,
                Level = w.Value.Level,
                IsChinese = w.Value.Language == VocabLanguage.Chinese,
                IsHidden = w.Value.Hidden,
                IsHard = !w.Value.Hidden && VocabQuiz.IsHard(w.Value, s),
                IsSlipping = !w.Value.Hidden && VocabQuiz.IsSlipping(w.Value, s),
                IsNew = s.IsNew,
                Strength = s.Level,
            });
            last = group;
        }
        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>The word <paramref name="step"/> places after (or before, when negative) this one in the list; null past the ends or when it is not listed.</summary>
    public string? Neighbour(string id, int step)
    {
        var at = _order.IndexOf(id);
        return at < 0 || at + step < 0 || at + step >= _order.Count ? null : _order[at + step];
    }

    /// <summary>Shows the page that holds this word (back from walking the words with Previous and Next).</summary>
    public void ShowPageOf(string id)
    {
        var at = _order.IndexOf(id);
        if (at < 0) return;
        Pages.ShowPageOf(at);
        Refresh();
    }

    /// <summary>"3 / 25": where the word is in the list; empty when it is not listed.</summary>
    public string Position(string id) => _order.IndexOf(id) is var at and >= 0 ? $"{at + 1} / {_order.Count}" : "";

    /// <summary>The group a word falls in under the current order.</summary>
    private string Group(VocabWord word, VocabWordStats stats, DateTimeOffset now) => SortIndex switch
    {
        1 => (now - word.CreatedAt).TotalDays switch { < 1 => "Today", < 7 => "This week", < 31 => "This month", _ => "Earlier" },
        2 => stats.IsNew ? "New" : stats.Level == 0 ? "Forgotten last time" : $"Level {stats.Level}",
        3 => stats.IsNew || stats.Due is not { } due || due <= now ? "Due now"
            : (due - now).TotalDays < 1 ? "Later today" : (due - now).TotalDays < 7 ? "This week" : "Later",
        _ => SortKey(word) is { Length: > 0 } key ? key[..1].ToUpperInvariant() : "#",
    };

    /// <summary>
    /// The filters this list offers: due now, new, often forgotten, pinyin slips and hidden when there are such words, then
    /// every level, topic and tag.
    /// </summary>
    private void RebuildFilters(IReadOnlyList<(VocabWord Word, VocabWordStats Stats)> words)
    {
        var list = words.Select(w => w.Word).ToList();
        var now = _store.Now;
        var filters = new List<VocabWordFilter> { new(AllWords, (_, _) => true) };
        void AddIfAny(string name, Func<VocabWord, VocabWordStats, bool> matches)
        {
            if (words.Any(w => matches(w.Word, w.Stats))) filters.Add(new(name, matches));
        }
        AddIfAny("Due now", (w, s) => !w.Hidden && s.IsDue(now));
        AddIfAny("New", (w, s) => !w.Hidden && s.IsNew);
        AddIfAny("Often forgotten", (w, s) => !w.Hidden && VocabQuiz.IsHard(w, s));
        AddIfAny("Pinyin slips", (w, s) => !w.Hidden && VocabQuiz.IsSlipping(w, s));
        if (list.Any(w => w.Hidden)) filters.Add(new("Hidden", (w, _) => w.Hidden));
        foreach (var level in list.Select(w => w.Level).Where(l => l.Length > 0).Distinct().OrderBy(VocabLevels.Rank))
            filters.Add(new($"Level {level}", (w, _) => w.Level == level));
        foreach (var topic in list.Select(w => w.Topic).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCulture))
            filters.Add(new($"Topic: {topic}", (w, _) => string.Equals(w.Topic, topic, StringComparison.CurrentCultureIgnoreCase)));
        foreach (var tag in list.SelectMany(w => w.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCulture))
            filters.Add(new($"#{tag}", (w, _) => w.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)));
        var names = filters.Select(f => f.Name).ToList();
        if (names.SequenceEqual(FilterNames)) return;
        // Keep the chosen filter when it is still there.
        var chosen = FilterNames.ElementAtOrDefault(FilterIndex);
        _rebuildingFilters = true;
        _filters = filters;
        FilterNames = names;
        FilterIndex = Math.Max(0, names.IndexOf(chosen ?? AllWords));
        _rebuildingFilters = false;
    }

    /// <summary>The chips: every filter with the number of searched words it keeps.</summary>
    private void RefreshChips(IReadOnlyList<(VocabWord Word, VocabWordStats Stats)> searched)
    {
        Chips.Clear();
        for (var i = 0; i < _filters.Count; i++)
        {
            var index = i;
            var filter = _filters[i];
            Chips.Add(new VocabFilterChip(filter.Name, searched.Count(w => filter.Matches(w.Word, w.Stats)), i == FilterIndex,
                new RelayCommand(() => FilterIndex = index)));
        }
    }

    /// <summary>Alphabetical: English by the word, Chinese by its pinyin without tone marks.</summary>
    internal static string SortKey(VocabWord word) =>
        TextSearch.Fold(word.Language == VocabLanguage.Chinese && word.Phonetic.Length > 0 ? word.Phonetic : word.Text);
}
