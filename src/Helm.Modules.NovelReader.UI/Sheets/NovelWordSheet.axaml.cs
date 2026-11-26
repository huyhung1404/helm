using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The tapped word's sheet (see the XAML). The cursor starts in the Vietnamese, all of it selected.</summary>
public partial class NovelWordSheet : UserControl
{
    private readonly WordEditorViewModel? _editor;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelWordSheet() => InitializeComponent();

    public NovelWordSheet(WordEditorViewModel editor, Action close)
    {
        _editor = editor;
        DataContext = editor;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        AttachedToVisualTree += (_, _) =>
        {
            editor.PropertyChanged += OnChanged;
            Arrange();
            Dispatcher.UIThread.Post(() =>
            {
                DraftBox.Focus();
                DraftBox.SelectAll();
            }, DispatcherPriority.Input);
        };
        DetachedFromVisualTree += (_, _) => editor.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WordEditorViewModel.SavedAs)) Arrange();
    }

    /// <summary>Read from here alone fills the row; with Remove they share it.</summary>
    private void Arrange() => Grid.SetColumnSpan(ReadButton, _editor?.HasSaved == true ? 1 : 2);

    private void Draft_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _editor is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) _editor.SavePhraseCommand.Execute(null);
        else _editor.SaveNameCommand.Execute(null);
        e.Handled = true;
    }
}
