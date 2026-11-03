using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Helm.Modules.Vocab.Views;

/// <summary>
/// The Edit word dialog (see the XAML); the page puts the cursor in its first box when it opens. The word's tags show
/// as chips: ✕ takes one off, Enter in the box adds what was typed (the editor keeps them as one comma list).
/// </summary>
public partial class VocabEditDialog : UserControl
{
    private VocabWordEditor? _editor;

    public VocabEditDialog()
    {
        InitializeComponent();
        EditorCard.DataContextChanged += (_, _) => Watch(EditorCard.DataContext as VocabWordEditor);
    }

    private void Watch(VocabWordEditor? editor)
    {
        if (_editor is not null) _editor.PropertyChanged -= OnEditorChanged;
        _editor = editor;
        if (editor is not null) editor.PropertyChanged += OnEditorChanged;
        ShowTags();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabWordEditor.Tags) or nameof(VocabWordEditor.IsOpen)) ShowTags();
    }

    private void ShowTags() => TagChips.ItemsSource = _editor is null ? null : VocabTags.Parse(_editor.Tags);

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || sender is not FrameworkElement { Tag: string tag }) return;
        _editor.Tags = string.Join(", ", VocabTags.Parse(_editor.Tags).Where(t => !string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _editor is null) return;
        e.Handled = true;
        var typed = TagBox.Text.Trim();
        if (typed.Length == 0) return;
        _editor.Tags = string.Join(", ", VocabTags.Parse(_editor.Tags + ", " + typed));
        TagBox.Text = "";
    }
}
