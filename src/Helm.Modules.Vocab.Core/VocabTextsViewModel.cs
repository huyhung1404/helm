using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;

namespace Helm.Modules.Vocab;

/// <summary>A pasted text in the list: its title, how many words it gave and where it stands.</summary>
public sealed class VocabSourceRow(string id, string title, string status, int words, bool isWaiting, IRelayCommand open, IRelayCommand remove)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>"Waiting for Claude Code", "12 words to look at", "5 words added".</summary>
    public string Status { get; } = status;

    public int Words { get; } = words;

    public bool IsWaiting { get; } = isWaiting;

    public IRelayCommand Open { get; } = open;

    public IRelayCommand Remove { get; } = remove;
}

/// <summary>A word an agent picked from a text: ticked to be added, or left out.</summary>
public sealed partial class VocabProposalRow : ObservableObject
{
    private readonly Action<VocabProposalRow, bool> _changed;

    public VocabProposalRow(VocabProposal proposal, Action<VocabProposalRow, bool> changed)
    {
        _changed = changed;
        Word = proposal.Word;
        Phonetic = proposal.Phonetic;
        Meaning = proposal.Meaning;
        Sentence = proposal.Sentence;
        Reason = proposal.Reason;
        IsAdded = proposal.AddedWordId is not null && proposal.ExistingWordId is null;
        AlreadyThere = proposal.ExistingWordId is not null;
        _isChosen = proposal.Chosen;
    }

    public string Word { get; }

    public string Phonetic { get; }

    public string Meaning { get; }

    /// <summary>The line of the text it comes from.</summary>
    public string Sentence { get; }

    public string Reason { get; }

    /// <summary>Added to the waiting words from this text.</summary>
    public bool IsAdded { get; }

    /// <summary>The user already had it: shown dimmed, not ticked.</summary>
    public bool AlreadyThere { get; }

    public bool CanTick => !IsAdded && !AlreadyThere;

    public bool HasReason => Reason.Length > 0;

    public bool HasPhonetic => Phonetic.Length > 0;

    [ObservableProperty] private bool _isChosen;

    partial void OnIsChosenChanged(bool value) => _changed(this, value);
}

/// <summary>
/// Texts the user pastes to learn words from (an article, song lyrics), shared by the Windows and Android pages: paste
/// one, an AI agent proposes its words, the user keeps or drops each and adds the ticked ones. UI thread only; store
/// changes (also from sync) are posted to it.
/// </summary>
public sealed partial class VocabTextsViewModel : ObservableObject
{
    private readonly VocabStore _store;
    private readonly IUiDispatcher _ui;
    private string? _openId;
    private bool _refreshing;

    public VocabTextsViewModel(VocabStore store, IUiDispatcher ui)
    {
        _store = store;
        _ui = ui;
        _store.Changed += (_, _) => _ui.Post(Refresh);
        Refresh();
    }

    /// <summary>The texts dialog is open (the list and the paste form, or one text).</summary>
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddTextCommand))] private string _newBody = "";

    [ObservableProperty] private string _newTitle = "";

    [ObservableProperty] private string? _message;

    public ObservableCollection<VocabSourceRow> Texts { get; } = [];

    public bool HasTexts => Texts.Count > 0;

    public ObservableCollection<VocabProposalRow> Proposals { get; } = [];

    /// <summary>One text is open (its words), not the list.</summary>
    public bool IsDetailOpen => _openId is not null;

    public bool IsListOpen => _openId is null;

    public string DetailTitle { get; private set; } = "";

    public string DetailBody { get; private set; } = "";

    /// <summary>The open text still waits for an agent to propose its words.</summary>
    public bool IsDetailWaiting { get; private set; }

    public bool HasProposals => Proposals.Count > 0;

    /// <summary>Words ticked and not added yet.</summary>
    public int ChosenCount => Proposals.Count(p => p.IsChosen && p.CanTick);

    public string AddSelectedText => ChosenCount switch { 0 => "Add selected", 1 => "Add 1 word", var n => $"Add {n} words" };

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    // ---- the dialog ----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Open()
    {
        _openId = null;
        IsOpen = true;
        Refresh();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Back()
    {
        _openId = null;
        Message = null;
        Refresh();
    }

    // ---- pasting -------------------------------------------------------------------------------------------------

    private bool CanAddText() => NewBody.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddText))]
    private void AddText()
    {
        try
        {
            var id = _store.AddSource(NewTitle, NewBody);
            NewBody = "";
            NewTitle = "";
            Message = null;
            _openId = id;
            Refresh();
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
    }

    private void OpenText(string id)
    {
        _openId = id;
        Message = null;
        Refresh();
    }

    private void RemoveText(string id)
    {
        _store.DeleteSource(id);
        if (_openId == id) _openId = null;
        Refresh();
    }

    // ---- the words of a text -------------------------------------------------------------------------------------

    private bool CanAddSelected() => ChosenCount > 0;

    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    private void AddSelected()
    {
        if (_openId is not { } id) return;
        var added = _store.AddChosen(id);
        Message = added == 1 ? "Added 1 word. Claude Code breaks it down next." : $"Added {added} words. Claude Code breaks them down next.";
    }

    [RelayCommand]
    private void SelectAll() => Tick(true);

    [RelayCommand]
    private void SelectNone() => Tick(false);

    private void Tick(bool chosen)
    {
        if (_openId is not { } id) return;
        foreach (var row in Proposals.Where(r => r.CanTick)) _store.SetChosen(id, row.Word, chosen);
    }

    private void OnTicked(VocabProposalRow row, bool chosen)
    {
        if (_refreshing || _openId is not { } id) return;
        _store.SetChosen(id, row.Word, chosen);
    }

    // ---- showing -------------------------------------------------------------------------------------------------

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            Texts.Clear();
            foreach (var s in _store.AllSources())
            {
                var id = s.Id;
                var added = s.Value.Proposals.Count(p => p.AddedWordId is not null && p.ExistingWordId is null);
                Texts.Add(new VocabSourceRow(id, Title(s.Value), StatusOf(s.Value, added), s.Value.Proposals.Count, s.Value.Status == VocabSourceStatus.Waiting,
                    new RelayCommand(() => OpenText(id)), new RelayCommand(() => RemoveText(id))));
            }

            if (_openId is { } open && _store.GetSource(open) is { } source)
            {
                DetailTitle = Title(source);
                DetailBody = source.Body;
                IsDetailWaiting = source.Status == VocabSourceStatus.Waiting;
                // Ticking a word writes to the store, which comes back here: keep the rows (and the focus on the box) when only a tick changed.
                if (Proposals.Count == source.Proposals.Count
                    && Proposals.Zip(source.Proposals).All(z => z.First.Word == z.Second.Word && z.First.IsAdded == (z.Second.AddedWordId is not null && z.Second.ExistingWordId is null)))
                {
                    foreach (var (row, p) in Proposals.Zip(source.Proposals)) row.IsChosen = p.Chosen;
                }
                else
                {
                    Proposals.Clear();
                    foreach (var p in source.Proposals)
                    {
                        var row = new VocabProposalRow(p, OnTicked);
                        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VocabProposalRow.IsChosen)) NotifyChosen(); };
                        Proposals.Add(row);
                    }
                }
            }
            else
            {
                Proposals.Clear();
                _openId = null;
                DetailTitle = "";
                DetailBody = "";
                IsDetailWaiting = false;
            }
        }
        finally
        {
            _refreshing = false;
        }
        OnPropertyChanged(nameof(HasTexts));
        OnPropertyChanged(nameof(IsDetailOpen));
        OnPropertyChanged(nameof(IsListOpen));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailBody));
        OnPropertyChanged(nameof(IsDetailWaiting));
        OnPropertyChanged(nameof(HasProposals));
        NotifyChosen();
    }

    private void NotifyChosen()
    {
        OnPropertyChanged(nameof(ChosenCount));
        OnPropertyChanged(nameof(AddSelectedText));
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    private static string Title(VocabSource source)
    {
        if (source.Title.Length > 0) return source.Title;
        var first = source.Body.Split('\n', 2)[0].Trim();
        return first.Length > 40 ? first[..40] + "…" : first;
    }

    private static string StatusOf(VocabSource source, int added) => source.Status switch
    {
        VocabSourceStatus.Waiting => "Waiting for Claude Code",
        VocabSourceStatus.Proposed => source.Proposals.Count == 1 ? "1 word to look at" : $"{source.Proposals.Count} words to look at",
        _ => added switch { 0 => "Done", 1 => "1 word added", _ => $"{added} words added" },
    };
}
