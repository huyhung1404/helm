using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>What a report records about where the reader was: the novel, the chapter and the converter it was read with.</summary>
public sealed record ReportContext(string BookId, string BookTitle, int Chapter, string ChapterTitle, Converter Converter);

/// <summary>
/// The form that reports a wrong translation: the sentence of the clicked word (widened or narrowed a sentence at a
/// time), what Helm made of it, a box for the right translation (Helm's text to start from) and a note. Also edits the
/// translation and note of a saved report.
/// </summary>
public sealed partial class ReportEditorViewModel : ObservableObject
{
    private readonly TranslationReportStore _store;
    private readonly Func<string> _device;
    private ConvertedLine? _line;
    private IReadOnlyList<ReportCapture.Range> _sentences = [];
    private int _first;
    private int _last;
    private int _anchor;
    private ReportContext? _context;
    private string? _editingId;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _chinese = "";
    [ObservableProperty] private string _helm = "";
    [ObservableProperty] private string _correct = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _suggestion = "";
    [ObservableProperty] private string _suggestionNote = "";
    [ObservableProperty] private string _errorKind = "";
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private string _askStatus = "";

    public ReportEditorViewModel(TranslationReportStore store, Func<string> device)
    {
        _store = store;
        _device = device;
    }

    public bool IsNew => _editingId is null;

    public string Title => IsNew ? "Report a wrong translation" : "Edit the report";

    public bool HasError => Error is not null;

    public bool HasSuggestion => Suggestion.Length > 0;

    public bool HasAskStatus => AskStatus.Length > 0;

    /// <summary>The report the form shows, once saved (null for a new one).</summary>
    public string? EditingId => _editingId;

    /// <summary>An AI can suggest the translation (the PC with Claude Code); set by the list's view model.</summary>
    public bool CanAsk { get; set; }

    /// <summary>Asks the AI about one saved report (set by the list's view model).</summary>
    public Func<string, Task>? Ask { get; set; }

    public bool CanAddBefore => IsNew && _first > 0;

    public bool CanAddAfter => IsNew && _last + 1 < _sentences.Count;

    public bool CanDrop => IsNew && _last > _first;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnSuggestionChanged(string value) => OnPropertyChanged(nameof(HasSuggestion));

    partial void OnAskStatusChanged(string value) => OnPropertyChanged(nameof(HasAskStatus));

    /// <summary>
    /// Opens the form on the sentence holding the clicked word (<paramref name="start"/>/<paramref name="length"/> in
    /// <paramref name="line"/>'s source). The paragraph is converted again as VietPhrase, whatever the page shows.
    /// </summary>
    public void Open(ConvertedLine line, int start, int length, ReportContext context)
    {
        var converted = context.Converter.ConvertLine(line.Source);
        // Converting a normalized line again gives the same positions; if it ever did not, the page's line stays right.
        _line = converted.Source == line.Source ? converted : line;
        _context = context;
        _editingId = null;
        _sentences = ReportCapture.Sentences(_line.Source);
        if (_sentences.Count == 0) return;
        _first = _last = _anchor = ReportCapture.SentenceAt(_sentences, start);
        Word = start >= 0 && length > 0 && start + length <= _line.Source.Length ? _line.Source.Substring(start, length).Trim() : "";
        Note = "";
        Correct = "";
        Error = null;
        ShowSuggestion(null);
        AskStatus = "";
        Select(_first, _last);
        RaiseShape();
        IsOpen = true;
    }

    /// <summary>Opens a saved report to change its right translation or its note.</summary>
    public void Edit(string id, TranslationReport report)
    {
        _line = null;
        _context = null;
        _sentences = [];
        _first = _last = _anchor = 0;
        _editingId = id;
        Chinese = report.Chinese;
        Helm = report.Helm;
        Word = report.Word;
        Correct = report.Correct.Length > 0 ? report.Correct : report.Helm;
        Note = report.Note;
        Error = null;
        ShowSuggestion(report);
        AskStatus = "";
        RaiseShape();
        IsOpen = true;
    }

    /// <summary>The saved report changed (an AI's suggestion arrived): shows its suggestion, keeping what the reader typed.</summary>
    public void Reload(TranslationReport report) => ShowSuggestion(report);

    /// <summary>
    /// Saves the form as it is (a new report without a right translation is fine here) and asks the AI about it; the
    /// form then edits that report and shows the suggestion when it comes.
    /// </summary>
    [RelayCommand]
    private async Task AskAi()
    {
        if (Ask is null || IsAsking) return;
        try
        {
            var id = Keep();
            if (id is not null) await Ask(id);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Puts the AI's suggestion in the box for the right translation.</summary>
    [RelayCommand]
    private void UseSuggestion()
    {
        if (HasSuggestion) Correct = Suggestion;
    }

    [RelayCommand]
    private void AddBefore()
    {
        if (CanAddBefore) Select(_first - 1, _last);
    }

    [RelayCommand]
    private void AddAfter()
    {
        if (CanAddAfter) Select(_first, _last + 1);
    }

    /// <summary>Back to fewer sentences: the one with the clicked word stays.</summary>
    [RelayCommand]
    private void Drop()
    {
        if (!CanDrop) return;
        if (_anchor > _first) Select(_first + 1, _last);
        else Select(_first, _last - 1);
    }

    [RelayCommand]
    private void Save()
    {
        var correct = Correct.Trim();
        // Helm's own text left as it was says nothing.
        if (correct == Helm) correct = "";
        var note = Note.Trim();
        if (correct.Length == 0 && note.Length == 0 && !HasSuggestion)
        {
            Error = "Write the right translation, or a note on what is wrong.";
            return;
        }
        try
        {
            if (_editingId is { } id)
            {
                _store.Update(id, correct, note);
            }
            else if (_line is { } line && _context is { } context)
            {
                _store.Add(NewReport(line, context, correct, note), _device());
            }
            IsOpen = false;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Saves the form without checking it and returns the report's id (the form then edits it).</summary>
    private string? Keep()
    {
        var correct = Correct.Trim() == Helm ? "" : Correct.Trim();
        if (_editingId is { } id)
        {
            _store.Update(id, correct, Note);
            return id;
        }
        if (_line is not { } line || _context is not { } context) return null;
        _editingId = _store.Add(NewReport(line, context, correct, Note.Trim()), _device());
        RaiseShape();
        return _editingId;
    }

    private TranslationReport NewReport(ConvertedLine line, ReportContext context, string correct, string note) => new()
    {
        BookId = context.BookId,
        BookTitle = context.BookTitle,
        Chapter = context.Chapter,
        ChapterTitle = context.ChapterTitle,
        Chinese = Chinese,
        Helm = Helm,
        Pieces = ReportCapture.PiecesOf(line, Span()),
        Correct = correct,
        Note = note,
        Word = Word,
        Paragraph = line.Source,
    };

    private void ShowSuggestion(TranslationReport? report)
    {
        Suggestion = report?.Suggestion ?? "";
        SuggestionNote = report?.SuggestionNote ?? "";
        ErrorKind = report?.ErrorKind ?? "";
    }

    private ReportCapture.Range Span() =>
        new(_sentences[_first].Start, _sentences[_last].End - _sentences[_first].Start);

    private void Select(int first, int last)
    {
        if (_line is not { } line) return;
        _first = first;
        _last = last;
        var range = Span();
        var before = Helm;
        Chinese = line.Source.Substring(range.Start, range.Length);
        Helm = ReportCapture.TextOf(line, range);
        // The box follows Helm's text until the reader types in it.
        if (Correct.Length == 0 || Correct == before) Correct = Helm;
        Error = null;
        RaiseShape();
    }

    private void RaiseShape()
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanAddBefore));
        OnPropertyChanged(nameof(CanAddAfter));
        OnPropertyChanged(nameof(CanDrop));
    }
}
