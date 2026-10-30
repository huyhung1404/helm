using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab;

/// <summary>A part with the user's words that have it: a family (lat → relative, translate, relate).</summary>
public sealed record VocabRootRow(string PartId, string Form, string Kind, string Meaning, string Origin, IReadOnlyList<VocabWordLink> Words,
    IRelayCommand Select)
{
    /// <summary>"Families" or "Used once" where that group starts; empty otherwise.</summary>
    public string Header { get; init; } = "";

    public bool HasHeader => Header.Length > 0;

    public string CountText => Words.Count == 1 ? "1 word" : $"{Words.Count} words";

    public bool HasOrigin => Origin.Length > 0;
}

/// <summary>
/// The Roots tab: every part of the user's words with the words that share it, the biggest families first; a card to
/// practise roots alone (the root, then its meaning and family); and, on the PC, "Suggest words" to add more of a
/// family. UI thread only.
/// </summary>
public sealed partial class VocabRootsViewModel : ObservableObject
{
    private readonly VocabStore _store;
    private readonly Action<string> _open;
    private readonly Random _random;

    [ObservableProperty] private VocabLanguage _language = VocabLanguage.English;
    [ObservableProperty] private VocabRootRow? _selected;

    // The practice card.
    [ObservableProperty] private VocabRootRow? _practice;
    [ObservableProperty] private bool _isPracticeRevealed;

    public VocabRootsViewModel(VocabStore store, VocabAgentViewModel agent, int pageSize, Action<string> open, Random? random = null)
    {
        _store = store;
        _open = open;
        _random = random ?? Random.Shared;
        Agent = agent;
        Pager = new ListPager(Refresh, pageSize);
    }

    public ObservableCollection<VocabRootRow> Roots { get; } = [];

    public ListPager Pager { get; }

    public VocabAgentViewModel Agent { get; }

    public bool IsEnglish => Language == VocabLanguage.English;

    public bool IsChinese => Language == VocabLanguage.Chinese;

    public bool HasRoots => Roots.Count > 0;

    public bool HasNoRoots => Roots.Count == 0;

    public bool HasSelected => Selected is not null;

    public bool HasPractice => Practice is not null;

    partial void OnLanguageChanged(VocabLanguage value)
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsChinese));
        Selected = null;
        Practice = null;
        Refresh();
    }

    partial void OnSelectedChanged(VocabRootRow? value) => OnPropertyChanged(nameof(HasSelected));

    partial void OnPracticeChanged(VocabRootRow? value)
    {
        OnPropertyChanged(nameof(HasPractice));
        IsPracticeRevealed = false;
    }

    [RelayCommand]
    private void ShowEnglish() => Language = VocabLanguage.English;

    [RelayCommand]
    private void ShowChinese() => Language = VocabLanguage.Chinese;

    /// <summary>A root to recall: a family's more often than a part used once.</summary>
    [RelayCommand]
    private void NextRoot()
    {
        var all = Families(_store, Language, _open);
        if (all.Count == 0)
        {
            Practice = null;
            return;
        }
        var pool = all.Where(r => r.Words.Count > 1 && r.PartId != Practice?.PartId).ToList();
        if (pool.Count == 0) pool = all.Where(r => r.PartId != Practice?.PartId).ToList();
        if (pool.Count == 0) pool = all;
        Practice = pool[_random.Next(pool.Count)];
    }

    [RelayCommand]
    private void RevealRoot() => IsPracticeRevealed = Practice is not null;

    [RelayCommand]
    private void ClosePractice() => Practice = null;

    [RelayCommand]
    private void CloseSelected() => Selected = null;

    /// <summary>Asks the agent for a few more words of the selected family.</summary>
    [RelayCommand]
    private Task SuggestAsync() =>
        Selected is { } root ? Agent.SuggestAsync(root.PartId, root.Form, root.Meaning, Language) : Task.CompletedTask;

    public void Refresh()
    {
        var all = Families(_store, Language, _open, row => Selected = row);
        Roots.Clear();
        var last = "";
        foreach (var row in Pager.Take(all, Language))
        {
            var group = row.Words.Count > 1 ? "Families" : "Used once";
            Roots.Add(group == last ? row : row with { Header = group });
            last = group;
        }
        if (Selected is { } chosen) Selected = all.FirstOrDefault(r => r.PartId == chosen.PartId);
        OnPropertyChanged(nameof(HasRoots));
        OnPropertyChanged(nameof(HasNoRoots));
    }

    /// <summary>
    /// Every part used by the analyzed words of a language, at any depth of their trees, with those words: the biggest
    /// families first, then alphabetically.
    /// </summary>
    internal static List<VocabRootRow> Families(VocabStore store, VocabLanguage language, Action<string> open, Action<VocabRootRow>? select = null)
    {
        var words = store.Words().Where(w => w.Value.IsAnalyzed && w.Value.Language == language).ToList();
        var byPart = new Dictionary<string, List<(string Id, VocabWord Word)>>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Walk(IEnumerable<VocabPartLink> links, int depth)
            {
                if (depth > VocabAnalyzer.MaxDepth) return;
                foreach (var link in links)
                {
                    if (!seen.Add(link.PartId) || store.GetPart(link.PartId) is not { } part) continue;
                    if (!byPart.TryGetValue(link.PartId, out var list)) byPart[link.PartId] = list = [];
                    list.Add((w.Id, w.Value));
                    Walk(part.Parts, depth + 1);
                }
            }
            Walk(w.Value.Parts, 0);
        }
        var rows = new List<VocabRootRow>();
        foreach (var (partId, family) in byPart)
        {
            if (store.GetPart(partId) is not { } part) continue;
            var links = family.OrderBy(f => f.Word.Text, StringComparer.CurrentCulture)
                .Select(f => new VocabWordLink(f.Id, f.Word.Text, f.Word.Meaning, new RelayCommand(() => open(f.Id)))).ToList();
            VocabRootRow? row = null;
            row = new VocabRootRow(partId, part.Form, KindText(part.Kind), part.Meaning, part.Origin, links,
                new RelayCommand(() => select?.Invoke(row!)));
            rows.Add(row);
        }
        return rows.OrderByDescending(r => r.Words.Count).ThenBy(r => r.Form, StringComparer.CurrentCulture).ToList();
    }

    private static string KindText(VocabPartKind kind) => kind switch
    {
        VocabPartKind.Prefix => "prefix",
        VocabPartKind.Root => "root",
        VocabPartKind.Suffix => "suffix",
        VocabPartKind.Word => "word",
        VocabPartKind.Character => "character",
        _ => "component",
    };
}
