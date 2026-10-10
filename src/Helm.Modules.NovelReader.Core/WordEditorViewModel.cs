using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Text;
using Helm.Shell.Services;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The popup of a clicked word, like sangtacviet's: the Chinese, its Hán Việt reading and the meanings the dictionaries
/// know, a box to type the right one, and buttons to widen or narrow the selection. Saving a name or a meaning (for this
/// novel or all of them) syncs it and converts the chapter again.
/// </summary>
public sealed partial class WordEditorViewModel : ObservableObject
{
    private readonly NovelStore _store;
    private readonly IClipboardService _clipboard;
    private readonly Func<Converter?> _converter;
    private readonly Func<string?> _bookId;
    private string _source = "";
    private int _start;
    private int _length;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _chinese = "";
    [ObservableProperty] private string _hanViet = "";
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _savedAs = "";
    [ObservableProperty] private bool _forAllNovels;
    [ObservableProperty] private string? _error;

    public WordEditorViewModel(NovelStore store, IClipboardService clipboard, Func<Converter?> converter, Func<string?> bookId)
    {
        _store = store;
        _clipboard = clipboard;
        _converter = converter;
        _bookId = bookId;
    }

    /// <summary>Meanings to pick from: the dictionaries' ones, then the Hán Việt reading as a name and as words.</summary>
    public ObservableCollection<string> Meanings { get; } = [];

    /// <summary>The paragraph the word is in (the page places the popup next to it).</summary>
    public ParagraphViewModel? Paragraph { get; private set; }

    public bool HasSaved => SavedAs.Length > 0;

    public bool HasError => Error is not null;

    public bool CanShrink => _length > 1;

    public bool CanExtendLeft => _start > 0;

    public bool CanExtendRight => _start + _length < _source.Length;

    partial void OnSavedAsChanged(string value) => OnPropertyChanged(nameof(HasSaved));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void Open(ParagraphViewModel paragraph, Segment segment)
    {
        if (paragraph.Line is not { } line) return;
        Paragraph = paragraph;
        _source = line.Source;
        _start = segment.Start;
        _length = Math.Max(1, segment.Length);
        // Spaces and punctuation around a word are not part of it.
        while (_length > 1 && char.IsWhiteSpace(_source[_start])) { _start++; _length--; }
        while (_length > 1 && char.IsWhiteSpace(_source[_start + _length - 1])) _length--;
        ForAllNovels = false;
        Refresh(segment.Text);
        IsOpen = true;
    }

    [RelayCommand]
    private void ExtendLeft() => Select(_start - 1, _length + 1);

    [RelayCommand]
    private void ExtendRight() => Select(_start, _length + 1);

    [RelayCommand]
    private void ShrinkLeft() => Select(_start + 1, _length - 1);

    [RelayCommand]
    private void ShrinkRight() => Select(_start, _length - 1);

    [RelayCommand]
    private void PickMeaning(string? meaning)
    {
        if (meaning is not null) Draft = meaning;
    }

    /// <summary>Saves the box as a name: it wins over the phrases around it and keeps its capitals.</summary>
    [RelayCommand]
    private void SaveName() => Save(EntryKind.Name);

    /// <summary>Saves the box as the meaning of the phrase (empty makes the phrase disappear, like "了").</summary>
    [RelayCommand]
    private void SavePhrase() => Save(EntryKind.Phrase);

    /// <summary>Removes what the popup says is saved: this novel's entry first, else the one for all novels.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (_bookId() is not { } bookId || !_store.RemoveEntry(bookId, Chinese)) _store.RemoveEntry(null, Chinese);
        IsOpen = false;
    }

    /// <summary>Starts reading aloud at a segment of a paragraph (set by the page view model).</summary>
    public Action<ParagraphViewModel, int>? ReadFrom { get; init; }

    /// <summary>Reads aloud from the sentence this word is in.</summary>
    [RelayCommand]
    private void ReadFromHere()
    {
        IsOpen = false;
        if (Paragraph?.Line is not { } line || ReadFrom is null) return;
        var segment = 0;
        for (var i = 0; i < line.Segments.Count; i++)
        {
            var s = line.Segments[i];
            if (_start >= s.Start && _start < s.Start + Math.Max(1, s.Length)) { segment = i; break; }
        }
        ReadFrom(Paragraph, segment);
    }

    [RelayCommand]
    private void CopyChinese() => _clipboard.SetText(Chinese);

    [RelayCommand]
    private void Close() => IsOpen = false;

    private void Save(EntryKind kind)
    {
        var value = Draft.Trim();
        if (kind == EntryKind.Name && value.Length == 0)
        {
            Error = "Type the name first.";
            return;
        }
        var bookId = ForAllNovels ? null : _bookId();
        try
        {
            // What is saved for this novel wins over what is saved for all, so other novels keep theirs.
            _store.SaveEntry(bookId, kind, Chinese, value);
            // Saved for all: this novel's own entry would hide it here.
            if (bookId is null && _bookId() is { } current) _store.RemoveEntry(current, Chinese);
            IsOpen = false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
        }
    }

    private void Select(int start, int length)
    {
        if (start < 0 || length < 1 || start + length > _source.Length) return;
        _start = start;
        _length = length;
        Refresh(null);
    }

    private void Refresh(string? shown)
    {
        Error = null;
        Chinese = _source.Substring(_start, _length);
        var converter = _converter();
        HanViet = converter?.HanVietOf(Chinese) ?? "";
        Current = shown ?? converter?.ConvertLine(Chinese).Text ?? Chinese;
        Draft = Current;

        Meanings.Clear();
        void Add(string meaning)
        {
            if (meaning.Length > 0 && !Meanings.Contains(meaning)) Meanings.Add(meaning);
        }
        if (converter is not null)
        {
            foreach (var entry in converter.EntriesFor(Chinese))
                foreach (var meaning in ChineseText.Meanings(entry.Value)) Add(meaning);
        }
        if (HanViet.Length > 0)
        {
            Add(ChineseText.TitleCase(HanViet));
            Add(HanViet);
        }

        var bookId = _bookId();
        SavedAs = DescribeSaved(bookId is null ? null : _store.GetEntry(bookId, EntryKind.Name, Chinese), "a name for this novel")
            ?? DescribeSaved(bookId is null ? null : _store.GetEntry(bookId, EntryKind.Phrase, Chinese), "a meaning for this novel")
            ?? DescribeSaved(_store.GetEntry(null, EntryKind.Name, Chinese), "a name for all novels")
            ?? DescribeSaved(_store.GetEntry(null, EntryKind.Phrase, Chinese), "a meaning for all novels")
            ?? "";
        OnPropertyChanged(nameof(CanShrink));
        OnPropertyChanged(nameof(CanExtendLeft));
        OnPropertyChanged(nameof(CanExtendRight));
    }

    private static string? DescribeSaved(NovelEntry? entry, string what) =>
        entry is null ? null : $"Saved as {what}: {(entry.Vietnamese.Length > 0 ? entry.Vietnamese : "(nothing)")}";
}
