using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Modules.NovelReader.Reports;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>The form for a wrong translation (see the XAML). The cursor starts in the right translation.</summary>
public partial class NovelReportForm : UserControl
{
    private readonly ReportEditorViewModel? _editor;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public NovelReportForm() => InitializeComponent();

    public NovelReportForm(ReportEditorViewModel editor)
    {
        _editor = editor;
        DataContext = editor;
        InitializeComponent();
        CloseButton.Click += (_, _) => editor.CloseCommand.Execute(null);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            CorrectBox.Focus();
            CorrectBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Ctrl+Enter saves the report.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _editor is not null)
        {
            _editor.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
