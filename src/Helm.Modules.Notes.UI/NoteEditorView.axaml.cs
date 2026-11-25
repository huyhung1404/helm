using Avalonia.Controls;

namespace Helm.Modules.Notes.Ui;

/// <summary>One open note (see <see cref="NotesView"/>): wide, "Make tasks" sits beside the text; on a phone, under the toolbar.</summary>
public partial class NoteEditorView : UserControl
{
    public NoteEditorView() => InitializeComponent();

    /// <summary>Phone: Back instead of X, the tasks panel under the toolbar at full width, the labels hidden.</summary>
    public void SetNarrow(bool narrow)
    {
        BackButton.IsVisible = narrow;
        CloseButton.IsVisible = !narrow;
        MakeTasksLabel.IsVisible = !narrow;
        Grid.SetColumn(TasksPane, narrow ? 0 : 1);
        Grid.SetRow(TasksPane, 1);
        Grid.SetRowSpan(TasksPane, narrow ? 1 : 2);
        TasksPane.Width = narrow ? double.NaN : 340;
        TasksPane.Margin = narrow ? new Avalonia.Thickness(16, 0, 16, 12) : new Avalonia.Thickness(0, 0, 16, 16);
        TasksPane.MaxHeight = narrow ? 420 : double.PositiveInfinity;
    }

    /// <summary>The cursor goes into the text, at its end, so typing goes on where it stopped.</summary>
    public void FocusBody()
    {
        BodyBox.Focus();
        BodyBox.CaretIndex = BodyBox.Text?.Length ?? 0;
    }

    public void FocusTitle() => TitleBox.Focus();
}
