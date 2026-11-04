using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.App.Android.ViewModels;

namespace Helm.App.Android.Views;

/// <summary>
/// The navigation drawer's motion and gestures (view glue). It slides in whole, background and shadow with it, over a
/// page that dims: fast then slowing down when it opens, slow then fast and shorter when it closes, as the Windows
/// menu does. A tap on the dimmed page closes it; holding a tool's row shows that tool's actions.
/// </summary>
internal sealed class DrawerController
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);
    private const double Slop = 10; // a finger that moves further is scrolling the list, not holding a row

    private readonly Border _drawer;
    private readonly TranslateTransform _shift;
    private readonly Border _scrim;
    private readonly Func<MainViewModel?> _viewModel;
    private readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    private NavEntry? _held;
    private Button? _heldRow;
    private Point _holdStart;
    private int _motion; // the latest open/close, so an earlier close does not hide a drawer opened again since

    public DrawerController(Border drawer, TranslateTransform shift, Border scrim, Func<MainViewModel?> viewModel)
    {
        _drawer = drawer;
        _shift = shift;
        _scrim = scrim;
        _viewModel = viewModel;

        scrim.PointerPressed += (_, _) =>
        {
            if (_viewModel() is { } vm) vm.IsPaneOpen = false;
        };
        _hold.Tick += (_, _) => Held();
        drawer.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        drawer.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        drawer.AddHandler(InputElement.PointerReleasedEvent, (_, _) => CancelHold(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Once the list's scroll gesture takes the finger, moves and the lift no longer reach the drawer: the gesture
        // itself (and the row losing the pointer, a direct event) ends the hold, or every swipe would show the sheet.
        drawer.AddHandler(InputElement.ScrollGestureEvent, (_, _) => CancelHold(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private double ClosedX => -(_drawer.Width + 24); // the shadow too

    public void Show(bool open)
    {
        var motion = ++_motion;
        var duration = open ? OpenDuration : CloseDuration;
        Easing easing = open ? new CubicEaseOut() : new CubicEaseIn();
        _shift.Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = duration, Easing = easing }];
        _scrim.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = easing }];
        if (open) _drawer.IsVisible = true;
        _shift.X = open ? 0 : ClosedX;
        _scrim.Opacity = open ? 1 : 0;
        if (!open)
        {
            CancelHold();
            DispatcherTimer.RunOnce(() =>
            {
                if (motion == _motion) _drawer.IsVisible = false;
            }, duration + TimeSpan.FromMilliseconds(20));
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelHold();
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not { } row) return;
        if (row.DataContext is not NavEntry { Module: not null } entry) return;
        _held = entry;
        _heldRow = row;
        row.PointerCaptureLost += OnRowCaptureLost;
        _holdStart = e.GetPosition(_drawer);
        _hold.Start();
    }

    private void OnRowCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelHold();

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_held is null) return;
        var at = e.GetPosition(_drawer);
        if (Math.Abs(at.X - _holdStart.X) > Slop || Math.Abs(at.Y - _holdStart.Y) > Slop) CancelHold();
    }

    /// <summary>The sheet is up before the finger lifts; the row's click that follows is ignored while it is.</summary>
    private void Held()
    {
        var entry = _held;
        CancelHold();
        if (entry is not null) _viewModel()?.ShowSheet(entry);
    }

    private void CancelHold()
    {
        _hold.Stop();
        _held = null;
        if (_heldRow is not null) _heldRow.PointerCaptureLost -= OnRowCaptureLost;
        _heldRow = null;
    }
}
