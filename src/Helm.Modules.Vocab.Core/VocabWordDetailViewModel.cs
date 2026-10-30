using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>Another of the user's words that has the selected part (a tap opens it).</summary>
/// <summary>A word to open, cut around the part it is listed for (re<b>lat</b>ive) when there is one.</summary>
public sealed record VocabWordLink(string Id, string Text, string Meaning, IRelayCommand Open)
{
    public VocabWordSplit Split { get; init; } = VocabWordSplit.None(Text);
}

/// <summary>
/// One word opened on the page: its fields, the tree, and the card of the box tapped in the tree (the word's own box
/// at first). UI thread only.
/// </summary>
public sealed partial class VocabWordDetailViewModel : ObservableObject
{
    private readonly VocabStore _store;
    private readonly Action<string> _open;
    private readonly VocabVoice? _voice;
    private readonly Action<string>? _notify;

    [ObservableProperty] private VocabNodeViewModel? _selectedNode;
    [ObservableProperty] private string _redoNote = "";
    [ObservableProperty] private bool _isAskingRedo;

    // Editing the tapped part by hand (the word's own fields: Editor).
    [ObservableProperty] private bool _isEditingPart;
    [ObservableProperty] private string _editPartPhonetic = "";
    [ObservableProperty] private string _editPartMeaning = "";
    [ObservableProperty] private string _editPartHanViet = "";
    [ObservableProperty] private string _editPartOrigin = "";

    public VocabWordDetailViewModel(VocabStore store, string id, VocabWord word, VocabWordStats stats, bool showTraditional, Action<string> open, string? selectPartId = null,
        VocabVoice? voice = null, bool showYouGlish = true, Action<string>? notify = null)
    {
        _store = store;
        _open = open;
        _voice = voice;
        _notify = notify;
        ShowYouGlish = showYouGlish;
        Id = id;
        Word = word;
        LevelText = VocabFormat.Level(stats, store.Now);
        IsHard = !word.Hidden && VocabQuiz.IsHard(word, stats);
        Root = new VocabNodeViewModel(VocabTree.Build(store, word), showTraditional, n => SelectedNode = n,
            addAsWord: AddPartAsWord, isWord: form => store.FindWord(form) is not null);
        Tree = [Root];
        SelectedNode = (selectPartId is null ? null : Root.SelfAndDescendants().FirstOrDefault(n => n.Node.PartId == selectPartId)) ?? Root;
        ShowTraditional = showTraditional && word.Traditional.Length > 0;
        Info = new VocabWordInfo(word);
        Editor = new VocabWordEditor(store, id);
    }

    /// <summary>Examples, collocations, mnemonic, confusables and labels.</summary>
    public VocabWordInfo Info { get; }

    /// <summary>The word's own fields, edited by hand.</summary>
    public VocabWordEditor Editor { get; }

    /// <summary>Something is being typed here (a sync must not throw it away).</summary>
    public bool IsEditing => Editor.IsOpen || IsEditingPart || IsAskingRedo;

    public string Id { get; }

    public bool CanSay => _voice?.CanSay == true;

    public bool ShowYouGlish { get; }

    /// <summary>A Chinese character or component can be said on its own (an English root alone sounds like nothing).</summary>
    public bool CanSayPart => CanSay && IsPartSelected && IsChinese;

    public VocabWord Word { get; }

    public VocabNodeViewModel Root { get; }

    /// <summary>The root alone, for a tree control's items source.</summary>
    public IReadOnlyList<VocabNodeViewModel> Tree { get; }

    public bool IsPending => !Word.IsAnalyzed;

    /// <summary>Left out of the cards; "Show in cards again" brings it back.</summary>
    public bool IsHidden => Word.Hidden;

    /// <summary>The user keeps forgetting it (an agent is asked for a new way to remember it).</summary>
    public bool IsHard { get; }

    /// <summary>The store reports the change, and the page rebuilds this view model.</summary>
    [RelayCommand]
    private void ToggleHidden() => _store.SetHidden(Id, !Word.Hidden);

    /// <summary>"I know this": back in a month.</summary>
    [RelayCommand]
    private void Know() => _store.RecordKnown(Id);

    public bool IsChinese => Word.Language == VocabLanguage.Chinese;

    public bool HasHanViet => Word.HanViet.Length > 0;

    public bool ShowTraditional { get; }

    public bool HasNote => Word.Note.Length > 0;

    public bool HasExplanation => Word.Explanation.Length > 0;

    public bool HasTree => Root.HasChildren;

    /// <summary>Waiting, and what for: a first analysis or a new one after the user's remark.</summary>
    public string PendingText => Word.AnalyzedAt is null
        ? "Waiting for an AI agent to break this word down. Ask Claude Code to analyze your new words."
        : $"Sent back for a new analysis: {Word.RedoNote}";

    public bool HasRedoNote => IsPending && Word.AnalyzedAt is not null;

    public string LevelText { get; }

    // ---- the card of the selected box ----------------------------------------------------------------------------

    public string CardTitle => SelectedNode?.Form ?? "";

    public string CardKind => SelectedNode?.Node switch
    {
        null => "",
        { PartId: null } => Word.Language == VocabLanguage.Chinese ? "Word · Chinese" : "Word · English",
        { Kind: { } kind } node => node.Role == VocabPartRole.Plain ? KindText(kind) : $"{KindText(kind)} · gives the {RoleWord(node.Role)}",
        _ => "",
    };

    public string CardPhonetic => SelectedNode?.Node.Phonetic ?? "";

    public string CardHanViet => SelectedNode?.Node.HanViet is { Length: > 0 } hv ? $"Hán Việt: {hv}" : "";

    public string CardMeaning => SelectedNode?.Node.Meaning ?? "";

    /// <summary>The part's origin, or the word's explanation for the word's own box.</summary>
    public string CardOrigin => SelectedNode?.Node.Origin ?? "";

    public bool HasCardPhonetic => CardPhonetic.Length > 0;

    public bool HasCardHanViet => CardHanViet.Length > 0;

    public bool HasCardOrigin => CardOrigin.Length > 0;

    /// <summary>The user's other words with the selected part: the family the part belongs to.</summary>
    public IReadOnlyList<VocabWordLink> AlsoIn { get; private set; } = [];

    public bool HasAlsoIn => AlsoIn.Count > 0;

    /// <summary>A part is tapped (the word's own box repeats what is shown above the tree, so it gets no card).</summary>
    public bool IsPartSelected => SelectedNode is { IsRoot: false };

    public bool IsWordSelected => !IsPartSelected;

    /// <summary>The tapped part is a word by itself: the part's card offers to add it (or open it).</summary>
    public bool CanAddSelectedAsWord => SelectedNode?.CanAddAsWord == true;

    public string SelectedAddText => SelectedNode?.AddAsWordText ?? "";

    [RelayCommand]
    private void AddSelectedAsWord()
    {
        if (SelectedNode is { CanAddAsWord: true } node) AddPartAsWord(node);
    }

    /// <summary>Makes a part (挽 of 挽昼) one of the user's words, waiting for an analysis; one they have already opens.</summary>
    private void AddPartAsWord(VocabNodeViewModel node)
    {
        var form = node.Node.Form;
        if (_store.FindWord(form) is { } existing)
        {
            _open(existing.Id);
            return;
        }
        try
        {
            _store.AddWord(form, $"Trong {Word.Text}");
            _notify?.Invoke($"“{form}” is one of your words now; it waits for an AI agent to break it down (Add tab).");
        }
        catch (ArgumentException ex)
        {
            _notify?.Invoke(ex.Message);
        }
    }

    partial void OnSelectedNodeChanged(VocabNodeViewModel? oldValue, VocabNodeViewModel? newValue)
    {
        IsEditingPart = false;
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        AlsoIn = newValue?.Node.PartId is { } partId
            ? _store.WordsUsing(partId).Where(w => w.Id != Id)
                .Select(w => new VocabWordLink(w.Id, w.Value.Text, w.Value.Meaning, new RelayCommand(() => _open(w.Id)))
                {
                    Split = VocabHighlight.Find(_store, w.Value, partId),
                }).ToList()
            : [];
        foreach (var name in new[] { nameof(CardTitle), nameof(CardKind), nameof(CardPhonetic), nameof(CardHanViet), nameof(CardMeaning), nameof(CardOrigin),
                     nameof(HasCardPhonetic), nameof(HasCardHanViet), nameof(HasCardOrigin), nameof(AlsoIn), nameof(HasAlsoIn),
                     nameof(IsPartSelected), nameof(IsWordSelected), nameof(CanSayPart), nameof(CanAddSelectedAsWord), nameof(SelectedAddText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private Task SayWordAsync() => _voice?.SayAsync(Word.Text, Word.Language) ?? Task.CompletedTask;

    [RelayCommand]
    private Task SayPartAsync() => SelectedNode is { } node && _voice is not null ? _voice.SayAsync(node.Node.Form, Word.Language) : Task.CompletedTask;

    [RelayCommand]
    private void YouGlish() => _voice?.OpenYouGlish(Word.Text, Word.Language);

    [RelayCommand]
    private void BeginEditPart()
    {
        if (SelectedNode?.Node.PartId is null || _store.GetPart(SelectedNode.Node.PartId) is not { } part) return;
        EditPartPhonetic = part.Phonetic;
        EditPartMeaning = part.Meaning;
        EditPartHanViet = part.HanViet;
        EditPartOrigin = part.Origin;
        IsEditingPart = true;
    }

    /// <summary>The part's new wording shows in every word that has it.</summary>
    [RelayCommand]
    private void SavePart()
    {
        if (SelectedNode?.Node.PartId is { } partId)
            _store.UpdatePart(partId, p => p with
            {
                Phonetic = EditPartPhonetic.Trim(),
                Meaning = EditPartMeaning.Trim(),
                HanViet = EditPartHanViet.Trim(),
                Origin = EditPartOrigin.Trim(),
            });
        IsEditingPart = false;
    }

    [RelayCommand]
    private void CancelEditPart() => IsEditingPart = false;

    [RelayCommand]
    private void BeginRedo() => IsAskingRedo = !IsAskingRedo;

    /// <summary>Back to the agents with the user's remark ("tách lat thành ferre").</summary>
    [RelayCommand]
    private void SendRedo()
    {
        _store.RequestRedo(Id, RedoNote);
        IsAskingRedo = false;
    }

    private static string KindText(VocabPartKind kind) => kind switch
    {
        VocabPartKind.Prefix => "Prefix",
        VocabPartKind.Root => "Root",
        VocabPartKind.Suffix => "Suffix",
        VocabPartKind.Word => "Word inside",
        VocabPartKind.Character => "Character",
        _ => "Component",
    };

    private static string RoleWord(VocabPartRole role) => role == VocabPartRole.Sound ? "sound" : "meaning";
}
