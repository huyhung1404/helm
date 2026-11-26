using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Helm.Modules.WindowZones.Editor;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>The zone editor sheet (see <see cref="WindowZonesView.OpenEditor"/>): Del deletes the chosen zone, Ctrl+Enter saves.</summary>
public partial class ZonesEditorView : UserControl
{
    private readonly EditorSession? _session;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public ZonesEditorView() => InitializeComponent();

    public ZonesEditorView(EditorSession session, WindowZonesViewModel viewModel)
    {
        _session = session;
        DataContext = session;
        InitializeComponent();
        if (session.Monitors.Count > 1)
            Hint.Text = "Drag on empty space to draw a zone, on any screen · drag a zone to move it, its edges to resize it · right-click deletes it";
        // The app's own editor over every monitor: this one closes without saving first.
        ScreensButton.Click += (_, _) =>
        {
            session.CloseCommand.Execute(null);
            viewModel.OpenEditorCommand.Execute(null);
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The session being edited (the Gallery and the tests pick a zone through it).</summary>
    public EditorSession? Session => _session;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A phone sheet: the full-size editor on its own line, Cancel and Save sharing the line under it.
        var narrow = e.NewSize.Width < 520;
        Footer.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,*" : "*,Auto,Auto");
        Grid.SetColumnSpan(ScreensButton, narrow ? 2 : 1);
        ScreensButton.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ScreensButton.HorizontalContentAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Grid.SetRow(CancelButton, narrow ? 1 : 0);
        Grid.SetColumn(CancelButton, narrow ? 0 : 1);
        Grid.SetRow(SaveButton, narrow ? 1 : 0);
        Grid.SetColumn(SaveButton, narrow ? 1 : 2);
        CancelButton.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SaveButton.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_session is null) return;
        switch (e.Key)
        {
            case Key.Delete when e.Source is not TextBox:
                e.Handled = true;
                _session.HandleDelete();
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _session.SaveCommand.Execute(null);
                break;
            case Key.Escape when _session.Edit.IsDragging:
                // Stop drawing first; a second Escape closes the sheet.
                e.Handled = true;
                _session.HandleEscape();
                break;
        }
    }
}
