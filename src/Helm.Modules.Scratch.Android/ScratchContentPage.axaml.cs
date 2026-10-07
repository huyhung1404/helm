using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core;
using Helm.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch itself, opened from the drawer and the Quick access tile (see <see cref="IModuleContent"/>). While the tool
/// is off the list stays visible but read-only, with a note pointing to the settings. Holding a card lifts it; dragging
/// it onto another card moves it there.
/// </summary>
public partial class ScratchContentPage : UserControl
{
    /// <summary>How long a finger rests on a card before it lifts (a shorter touch is a tap or a scroll).</summary>
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);

    /// <summary>Moving further than this before the card lifts is a scroll.</summary>
    private const double Slop = 10;

    private readonly ScratchModule _module;
    private readonly ScratchViewModel _viewModel;
    private readonly DispatcherTimer _hold;
    private ScratchRowViewModel? _pressed;
    private IPointer? _pointer;
    private Point _pressedAt;
    private Control? _lifted;

    /// <summary>For the XAML runtime loader and the designer; the shell resolves the page from DI.</summary>
    public ScratchContentPage()
        : this(HelmAndroidServices.Current.GetRequiredService<ScratchModule>(), HelmAndroidServices.Current.GetRequiredService<ScratchViewModel>())
    {
    }

    public ScratchContentPage(ScratchModule module, ScratchViewModel viewModel)
    {
        _module = module;
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _hold = new DispatcherTimer { Interval = HoldTime };
        _hold.Tick += (_, _) => Lift();
        // Tunnel and handled-too: the card's content is a button, which handles the press itself.
        Wall.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Wall.AddHandler(PointerCaptureLostEvent, (_, _) => Drop(null), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // Pages are transient and the module is a singleton: listen only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _module.PropertyChanged += OnModuleChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ApplyEnabled();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _module.PropertyChanged -= OnModuleChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        Drop(null);
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Reordering: hold a card, drag it onto another one --------------------------------------------------------

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Drop(null);
        if (!_viewModel.CanReorder || !_module.IsEnabled || InsideSmallButton(e.Source as Visual)) return;
        _pressed = RowAt(e.Source as Visual);
        if (_pressed is null) return;
        _pointer = e.Pointer;
        _pressedAt = e.GetPosition(Wall);
        _hold.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(Wall);
        if (_lifted is null)
        {
            // Moving before the card lifted: a scroll, not a drag.
            if (_pressed is not null && Distance(at, _pressedAt) > Slop) Cancel();
            return;
        }
        _lifted.RenderTransform = new TranslateTransform(at.X - _pressedAt.X, at.Y - _pressedAt.Y);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lifted is null)
        {
            Cancel();
            return;
        }
        // The release ends the drag: the card's button must not take it as a tap.
        e.Handled = true;
        Drop(e.GetPosition(Wall));
    }

    private void Lift()
    {
        _hold.Stop();
        if (_pressed is null || Wall.ContainerFromItem(_pressed) is not Control card) return;
        _lifted = card;
        card.Opacity = 0.75;
        card.ZIndex = 1;
        _pointer?.Capture(Wall);
    }

    /// <summary>Puts the lifted card down: on the card under <paramref name="at"/> (moves it there), or back in its place.</summary>
    private void Drop(Point? at)
    {
        _hold.Stop();
        var card = _lifted;
        var row = _pressed;
        _lifted = null;
        _pressed = null;
        if (card is null) return;
        card.RenderTransform = null;
        card.Opacity = 1;
        card.ZIndex = 0;
        if (_pointer?.Captured == Wall) _pointer.Capture(null);
        _pointer = null;
        if (at is { } point && row is not null && TargetAt(point, card) is { } target) _viewModel.Move(row.Id, target.Id);
    }

    private void Cancel()
    {
        _hold.Stop();
        _pressed = null;
        _pointer = null;
    }

    /// <summary>The card under a point of the wall, other than the one being dragged.</summary>
    private ScratchRowViewModel? TargetAt(Point point, Control dragged)
    {
        if (Wall.ItemsPanelRoot is not { } panel) return null;
        var inPanel = Wall.TranslatePoint(point, panel) ?? point;
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child, dragged) || !child.Bounds.Contains(inPanel)) continue;
            return child.DataContext as ScratchRowViewModel;
        }
        return null;
    }

    private static ScratchRowViewModel? RowAt(Visual? element)
    {
        for (var current = element; current is not null; current = current.GetVisualParent())
            if (current is StyledElement { DataContext: ScratchRowViewModel row }) return row;
        return null;
    }

    /// <summary>The card's own buttons (Copy, Delete) stay buttons.</summary>
    private static bool InsideSmallButton(Visual? element)
    {
        for (var current = element; current is not null; current = current.GetVisualParent())
        {
            if (current is Button button && button.Classes.Contains("small")) return true;
            if (current is ItemsControl) return false;
        }
        return false;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- Page --------------------------------------------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScratchViewModel.IsTextBoxOpen) && _viewModel.IsTextBoxOpen)
            Dispatcher.UIThread.Post(() => TextBox.Focus(), DispatcherPriority.Background);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
