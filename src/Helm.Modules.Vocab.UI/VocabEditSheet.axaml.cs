using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>Editing a word by hand (see the XAML); open while its <see cref="VocabWordEditor.IsOpen"/>.</summary>
public partial class VocabEditSheet : UserControl
{
    private readonly VocabWordEditor? _editor;
    private readonly string _opened = "";

    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabEditSheet() => InitializeComponent();

    public VocabEditSheet(VocabWordDetailViewModel detail)
    {
        _editor = detail.Editor;
        DataContext = detail;
        InitializeComponent();
        _opened = Fields(detail.Editor);
    }

    /// <summary>Nothing typed since it opened: a tap outside may close it without losing anything.</summary>
    public bool IsUnchanged => _editor is null || Fields(_editor) == _opened;

    public void FocusFirst() => MeaningBox.Focus();

    private static string Fields(VocabWordEditor e) => string.Join("\u0001", e.Phonetic, e.Meaning, e.HanViet, e.Traditional, e.Explanation, e.Note, e.Mnemonic,
        e.PinyinMnemonic, e.Examples, e.Collocations, e.Confusables, e.Level, e.PartOfSpeech, e.Topic, e.Tags, e.Forms, e.Synonyms, e.Antonyms);
}
