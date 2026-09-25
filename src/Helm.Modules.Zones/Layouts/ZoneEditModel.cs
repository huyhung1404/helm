using Helm.Core.Geometry;
using static Helm.Modules.Zones.Layouts.LayoutGeometry;

namespace Helm.Modules.Zones.Layouts;

/// <summary>
/// Editing state for one custom layout, in global physical pixels so a zone can be drawn across monitors.
/// Press on empty space draws a zone, on a zone body moves it, on a zone edge/corner resizes it; every drag snaps
/// to other zones and to monitor edges. Pure logic — the editor windows only forward mouse positions.
/// </summary>
public sealed class ZoneEditModel
{
    private readonly IReadOnlyList<PixelRect> _workAreas;
    private readonly int _snap;
    private Edges _action;
    private PixelPoint _pressPoint;
    private PixelRect _original;

    public ZoneEditModel(PixelRect reference, IReadOnlyList<PixelRect> workAreas, IEnumerable<PixelRect> zones, int snapDistance = 12)
    {
        Reference = reference;
        _workAreas = workAreas;
        _snap = snapDistance;
        Zones = zones.Select(z => Clamp(z, reference)).ToList();
    }

    /// <summary>Everything zones may occupy (one monitor, or the union of several).</summary>
    public PixelRect Reference { get; }

    public List<PixelRect> Zones { get; }

    public int Selected { get; private set; } = -1;

    /// <summary>The zone being drawn (not yet added), for rendering.</summary>
    public PixelRect? Drawing { get; private set; }

    public bool IsDragging => _action != Edges.None || Drawing is not null;

    public event EventHandler? Changed;

    /// <summary>Topmost zone under <paramref name="p"/> (later zones are drawn on top) and the grabbed part.</summary>
    public (int Index, Edges Part) HitTest(PixelPoint p, int grip)
    {
        for (var i = Zones.Count - 1; i >= 0; i--)
        {
            var z = Zones[i];
            if (!z.Inflate(grip / 2, grip / 2).Contains(p)) continue;
            var part = Edges.None;
            if (Math.Abs(p.X - z.Left) <= grip) part |= Edges.Left;
            else if (Math.Abs(p.X - z.Right) <= grip) part |= Edges.Right;
            if (Math.Abs(p.Y - z.Top) <= grip) part |= Edges.Top;
            else if (Math.Abs(p.Y - z.Bottom) <= grip) part |= Edges.Bottom;
            return (i, part == Edges.None ? Edges.Move : part);
        }
        return (-1, Edges.None);
    }

    public void Press(PixelPoint p, int grip)
    {
        _pressPoint = p;
        var (index, part) = HitTest(p, grip);
        if (index >= 0)
        {
            Selected = index;
            _original = Zones[index];
            _action = part;
        }
        else if (Reference.Contains(p))
        {
            Selected = -1;
            _action = Edges.None;
            Drawing = PixelRect.FromSize(p.X, p.Y, 0, 0);
        }
        Raise();
    }

    public void Drag(PixelPoint p)
    {
        if (Drawing is not null)
        {
            var raw = new PixelRect(Math.Min(_pressPoint.X, p.X), Math.Min(_pressPoint.Y, p.Y), Math.Max(_pressPoint.X, p.X), Math.Max(_pressPoint.Y, p.Y));
            // Snap every edge on its own (all four flags together would mean "move"), so both the starting corner
            // and the dragged corner land on nearby zone/monitor edges.
            var snapped = Snap(Snap(raw, Edges.Left | Edges.Top, Selected), Edges.Right | Edges.Bottom, Selected);
            Drawing = Intersect(snapped, Reference);
            Raise();
            return;
        }
        if (_action == Edges.None || Selected < 0) return;

        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        PixelRect next;
        if (_action == Edges.Move)
        {
            next = Clamp(Snap(_original.Offset(dx, dy), Edges.Move, Selected), Reference);
        }
        else
        {
            int left = _original.Left, top = _original.Top, right = _original.Right, bottom = _original.Bottom;
            if (_action.HasFlag(Edges.Left)) left += dx;
            if (_action.HasFlag(Edges.Right)) right += dx;
            if (_action.HasFlag(Edges.Top)) top += dy;
            if (_action.HasFlag(Edges.Bottom)) bottom += dy;
            var snapped = Snap(new PixelRect(left, top, right, bottom), _action, Selected);
            left = Math.Clamp(snapped.Left, Reference.Left, _original.Right - MinZonePixels);
            right = Math.Clamp(snapped.Right, _original.Left + MinZonePixels, Reference.Right);
            top = Math.Clamp(snapped.Top, Reference.Top, _original.Bottom - MinZonePixels);
            bottom = Math.Clamp(snapped.Bottom, _original.Top + MinZonePixels, Reference.Bottom);
            if (!_action.HasFlag(Edges.Left)) left = _original.Left;
            if (!_action.HasFlag(Edges.Right)) right = _original.Right;
            if (!_action.HasFlag(Edges.Top)) top = _original.Top;
            if (!_action.HasFlag(Edges.Bottom)) bottom = _original.Bottom;
            next = new PixelRect(left, top, right, bottom);
        }
        Zones[Selected] = next;
        Raise();
    }

    public void Release()
    {
        if (Drawing is { } drawn)
        {
            Drawing = null;
            if (drawn.Width >= MinZonePixels && drawn.Height >= MinZonePixels)
            {
                Zones.Add(drawn);
                Selected = Zones.Count - 1;
            }
        }
        _action = Edges.None;
        Raise();
    }

    public void Select(int index)
    {
        Selected = index >= 0 && index < Zones.Count ? index : -1;
        Raise();
    }

    public bool DeleteSelected()
    {
        if (Selected < 0) return false;
        Zones.RemoveAt(Selected);
        Selected = -1;
        Raise();
        return true;
    }

    /// <summary>Replaces the selected zone by its two halves (the first half stays selected).</summary>
    public bool SplitSelected(SplitOrientation orientation)
    {
        if (Selected < 0) return false;
        var zone = Zones[Selected];
        if ((orientation == SplitOrientation.Vertical ? zone.Width : zone.Height) < 2 * MinZonePixels) return false;
        var (first, second) = Split(zone, orientation);
        Zones[Selected] = first;
        Zones.Insert(Selected + 1, second);
        Raise();
        return true;
    }

    /// <summary>Removes every zone and adds one per work area (a quick start for re-drawing).</summary>
    public void ResetToWorkAreas()
    {
        Zones.Clear();
        foreach (var area in _workAreas)
            if (Intersect(area, Reference) is { IsEmpty: false } r) Zones.Add(r);
        Selected = -1;
        Raise();
    }

    /// <summary>Fractions of <see cref="Reference"/>, sorted left-to-right then top-to-bottom for stable zone numbers.</summary>
    public List<ZoneRect> ToLayoutZones() =>
        Zones.OrderBy(z => z.Top / 50).ThenBy(z => z.Left).ThenBy(z => z.Top)
            .Select(z => FromPixels(z, Reference)).ToList();

    private PixelRect Snap(PixelRect rect, Edges edges, int except)
    {
        var (xs, ys) = SnapLines(Zones.Where((_, i) => i != except), _workAreas.Append(Reference));
        return LayoutGeometry.Snap(rect, edges, xs, ys, _snap);
    }

    private static PixelRect Intersect(PixelRect a, PixelRect b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Max(Math.Max(a.Left, b.Left), Math.Min(a.Right, b.Right)), Math.Max(Math.Max(a.Top, b.Top), Math.Min(a.Bottom, b.Bottom)));

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
