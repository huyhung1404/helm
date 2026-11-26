using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Helm.Shell.Views;

/// <summary>
/// Dragging tabs, like a browser's: along the strip the tab moves under the cursor; up or down off the strip it leaves
/// for a window of its own (<see cref="Shell.Tabs.TabSet.TearOff"/>, the app opens the window under the cursor); the
/// only tab of a torn-off window drags that window instead. A middle click closes a tab; a left click shows it.
/// </summary>
public sealed class TabDrag
{
    /// <summary>How far off the strip (DIPs) a dragged tab must go before it leaves for a window of its own.</summary>
    public const double TearDistance = 24;

    private const double Slop = 4;

    private readonly Control _strip;
    private readonly ItemsControl _list;
    private readonly Func<TabStripViewModel?> _model;
    private TabItemViewModel? _pressed;
    private PointerPressedEventArgs? _press;
    private Point _pressedAt;
    private bool _dragging;

    public TabDrag(Control strip, ItemsControl list, Func<TabStripViewModel?> model)
    {
        _strip = strip;
        _list = list;
        _model = model;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => End(), RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => End(), RoutingStrategies.Bubble);
    }

    /// <summary>The tab left the strip (torn off): where the cursor held it, relative to the tab's top-left corner.</summary>
    public event EventHandler<Point>? TornOff;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || Header(source) is not { DataContext: TabItemViewModel tab }) return;
        var point = e.GetCurrentPoint(_list);
        if (point.Properties.IsMiddleButtonPressed)
        {
            tab.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        tab.ShowCommand.Execute(null);
        _pressed = tab;
        _press = e;
        _pressedAt = e.GetPosition(_list);
        _dragging = false;
        // The list holds the pointer, so the tab can move under the cursor while it is dragged.
        e.Pointer.Capture(_list);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } tab || _model() is not { } strip) return;
        var p = e.GetPosition(_list);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressedAt.X) < Slop && Math.Abs(p.Y - _pressedAt.Y) < Slop) return;
            _dragging = true;
            // The only tab of a torn-off window: dragging it drags the window.
            if (strip.Tabs.Count == 1 && !strip.Strip.IsMain && TopLevel.GetTopLevel(_strip) is Window window && _press is { } press)
            {
                End();
                window.BeginMoveDrag(press);
                return;
            }
        }

        var y = e.GetPosition(_strip).Y;
        if (y < -TearDistance || y > _strip.Bounds.Height + TearDistance)
        {
            if (!strip.Set.CanTearOff(tab.Tab)) return;
            var grab = _pressedAt - HeaderLeft(tab);
            End();
            if (strip.Set.TearOff(tab.Tab) is not null) TornOff?.Invoke(this, new Point(grab.X, grab.Y));
            return;
        }
        strip.Strip.MoveTo(tab.Tab, IndexAt(p.X, tab));
    }

    /// <summary>Before the first other tab whose middle is right of <paramref name="x"/>.</summary>
    private int IndexAt(double x, TabItemViewModel except)
    {
        var index = 0;
        foreach (var header in Headers())
        {
            if (ReferenceEquals(header.DataContext, except)) continue;
            var left = header.TranslatePoint(default, _list)?.X ?? 0;
            if (left + header.Bounds.Width / 2 < x) index++;
        }
        return index;
    }

    private Point HeaderLeft(TabItemViewModel tab) =>
        Headers().FirstOrDefault(h => ReferenceEquals(h.DataContext, tab))?.TranslatePoint(default, _list) ?? default;

    private IEnumerable<Border> Headers() =>
        _list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tab"));

    private static Border? Header(Visual source) =>
        source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tab"));

    private void End()
    {
        _pressed = null;
        _press = null;
        _dragging = false;
    }
}
