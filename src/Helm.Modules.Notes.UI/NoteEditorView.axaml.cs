using Avalonia.Controls;

namespace Helm.Modules.Notes.Ui;

/// <summary>One open note (see <see cref="NotesView"/>).</summary>
public partial class NoteEditorView : UserControl
{
    public NoteEditorView() => InitializeComponent();

    /// <summary>Phone: Back instead of X.</summary>
    public void SetNarrow(bool narrow)
    {
        BackButton.IsVisible = narrow;
        CloseButton.IsVisible = !narrow;
    }

    /// <summary>The cursor goes into the text, at its end, so typing goes on where it stopped.</summary>
    public void FocusBody()
    {
        BodyBox.Focus();
        BodyBox.CaretIndex = BodyBox.Text?.Length ?? 0;
    }

    public void FocusTitle() => TitleBox.Focus();
}
