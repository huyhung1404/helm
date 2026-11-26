using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Helm.Shell.Views;

/// <summary>
/// The shell's left menu (see <see cref="NavMenuViewModel"/>). View glue only: the icons-only look while closed, the
/// right edge that resizes it, and the hover that slides the icon rail open (<see cref="MenuPeek"/>).
/// </summary>
public partial class NavMenuView : UserControl
{
    private NavMenuViewModel? _model;
    private double? _dragFrom;
    private double _widthFrom;

    public NavMenuView()
    {
        InitializeComponent();
        DrawerIcon.Source = ShellAssets.AppIcon;
        Peek = new MenuPeek(this, () => _model);
        Grip.PointerPressed += OnGripPressed;
        Grip.PointerMoved += OnGripMoved;
        Grip.PointerReleased += (_, _) => _dragFrom = null;
        Grip.PointerCaptureLost += (_, _) => _dragFrom = null;
    }

    /// <summary>Opens the rail while the mouse rests on it (or on another trigger, the title bar's icon).</summary>
    public MenuPeek Peek { get; }

    /// <summary>The resize grip is being dragged (the rail stays open meanwhile).</summary>
    public bool IsResizing => _dragFrom is not null;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as NavMenuViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        UpdateLook();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateLook();

    private void UpdateLook()
    {
        var open = _model?.IsOpen ?? true;
        Classes.Set("rail", !open);
        Grip.IsVisible = _model is { IsOpen: true, IsDrawer: false };
        if (_model is not null) Grip.Margin = new Thickness(_model.OpenWidth - Grip.Width / 2, 0, 0, 0);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragFrom = e.GetPosition(this).X;
        _widthFrom = _model.OpenWidth;
        e.Pointer.Capture(Grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from || _model is null) return;
        // The width follows the mouse directly while dragging (no easing behind the cursor).
        Surface.Transitions = null;
        _model.OpenWidth = _widthFrom + e.GetPosition(this).X - from;
        RestoreTransitions();
    }

    private void RestoreTransitions()
    {
        if (Surface.Transitions is not null) return;
        Surface.Transitions =
        [
            new Avalonia.Animation.DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        ];
    }
}
