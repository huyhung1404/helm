using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Modules.WindowZones.Editor;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>
/// The full-screen zone editor on one monitor (Windows shows one per monitor, each in a window that covers it, all on
/// one <see cref="EditorSession"/>). Esc stops drawing or cancels, Del deletes the chosen zone, Ctrl+Enter saves.
/// </summary>
public partial class ZoneScreenEditorView : UserControl
{
    /// <summary>
    /// The see-through veil over the desktop, the same dark in both themes so the zones stand out (the toolbar keeps the
    /// app's theme). Set here, not in a theme key: only this editor covers other apps.
    /// </summary>
    private static readonly IBrush Veil = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10));

    private readonly EditorSession? _session;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public ZoneScreenEditorView() => InitializeComponent();

    public ZoneScreenEditorView(EditorSession session, MonitorInfo monitor, bool isHome)
    {
        _session = session;
        Monitor = monitor;
        DataContext = session;
        InitializeComponent();
        Background = Veil;
        Canvas.Viewport = monitor.Bounds;
        Toolbar.IsVisible = isHome;
        Caption.IsVisible = !isHome;
        CaptionText.Text = $"{monitor.DisplayName}: draw zones here too. The toolbar is on {session.Home.DisplayName}.";
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        // The keyboard goes to the canvas, so Esc, Del and Ctrl+Enter work at once.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Canvas.Focus(), DispatcherPriority.Input);
    }

    /// <summary>The monitor this view covers.</summary>
    public MonitorInfo? Monitor { get; }

    /// <summary>The session being edited (the Gallery and the tests pick a zone through it).</summary>
    public EditorSession? Session => _session;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A small screen: Cancel and Save share the line under the zone actions.
        var narrow = e.NewSize.Width < 720;
        Classes.Set("narrow", narrow);
        Actions.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,*" : "*,Auto,Auto");
        Grid.SetColumnSpan(ZoneActions, narrow ? 2 : 1);
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
            case Key.Escape:
                e.Handled = true;
                _session.HandleEscape();
                break;
            case Key.Delete when e.Source is not TextBox:
                e.Handled = true;
                _session.HandleDelete();
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _session.SaveCommand.Execute(null);
                break;
        }
    }
}
