using Helm.Core.Geometry;
using static Helm.Modules.WindowZones.Model.ZoneGeometry;

namespace Helm.Modules.WindowZones.Model;

/// <summary>A zone while it is edited, in global physical pixels. The id stays, so its windows follow it.</summary>
public sealed class EditZone(string id, PixelRect rect)
{
    public string Id { get; } = id;
    public PixelRect Rect { get; set; } = rect;
}

/// <summary>
/// Editing state for every zone on every monitor, in global physical pixels. Press on empty space draws a zone, on a
/// zone moves it, on its edge or corner resizes it; every drag snaps to other zones and to the monitor edges. A zone
/// always stays on one monitor: drawing keeps to the monitor it started on, moving follows the cursor to another one.
/// Pure logic — the editor windows only forward mouse positions.
/// </summary>
public sealed class ZoneEditModel
{
    private readonly int _snap;
    private Edges _action;
    private PixelPoint _pressPoint;
    private PixelRect _original;
    private int _drawingArea = -1;

    public ZoneEditModel(IReadOnlyList<PixelRect> workAreas, IEnumerable<EditZone> zones, int snapDistance = 12)
    {
        WorkAreas = workAreas;
        _snap = snapDistance;
        foreach (var z in zones)
        {
            var area = MostOverlapping(workAreas, z.Rect);
            if (area < 0) continue; // its monitor is not connected: keep it out of the editor (and of the save)
            Zones.Add(new EditZone(z.Id, Clamp(z.Rect, workAreas[area])));
        }
    }

    /// <summary>The work area of every connected monitor; zones live inside them.</summary>
    public IReadOnlyList<PixelRect> WorkAreas { get; }

    public List<EditZone> Zones { get; } = [];

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
            var z = Zones[i].Rect;
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

    public bool CanDrawAt(PixelPoint p) => AreaAt(p) >= 0;

    public void Press(PixelPoint p, int grip)
    {
        _pressPoint = p;
        var (index, part) = HitTest(p, grip);
        if (index >= 0)
        {
            Selected = index;
            _original = Zones[index].Rect;
            _action = part;
        }
        else if (AreaAt(p) is var area and >= 0)
        {
            Selected = -1;
            _action = Edges.None;
            _drawingArea = area;
            Drawing = PixelRect.FromSize(p.X, p.Y, 0, 0);
        }
        Raise();
    }

    public void Drag(PixelPoint p)
    {
        if (Drawing is not null)
        {
            var bounds = WorkAreas[_drawingArea];
            var raw = new PixelRect(Math.Min(_pressPoint.X, p.X), Math.Min(_pressPoint.Y, p.Y), Math.Max(_pressPoint.X, p.X), Math.Max(_pressPoint.Y, p.Y));
            // Snap every edge on its own (all four flags together would mean "move"), so both the starting corner
            // and the dragged corner land on nearby zone/monitor edges.
            var snapped = SnapTo(SnapTo(raw, Edges.Left | Edges.Top, -1), Edges.Right | Edges.Bottom, -1);
            Drawing = Intersect(snapped, bounds);
            Raise();
            return;
        }
        if (_action == Edges.None || Selected < 0) return;

        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        PixelRect next;
        if (_action == Edges.Move)
        {
            // The zone goes to the monitor under the cursor (it may change monitors), never across two.
            var area = AreaAt(p) is var a and >= 0 ? a : MostOverlapping(WorkAreas, _original);
            next = Clamp(SnapTo(_original.Offset(dx, dy), Edges.Move, Selected), WorkAreas[area]);
        }
        else
        {
            var bounds = WorkAreas[Math.Max(0, MostOverlapping(WorkAreas, _original))];
            int left = _original.Left, top = _original.Top, right = _original.Right, bottom = _original.Bottom;
            if (_action.HasFlag(Edges.Left)) left += dx;
            if (_action.HasFlag(Edges.Right)) right += dx;
            if (_action.HasFlag(Edges.Top)) top += dy;
            if (_action.HasFlag(Edges.Bottom)) bottom += dy;
            var snapped = SnapTo(new PixelRect(left, top, right, bottom), _action, Selected);
            left = Math.Clamp(snapped.Left, bounds.Left, _original.Right - MinZonePixels);
            right = Math.Clamp(snapped.Right, _original.Left + MinZonePixels, bounds.Right);
            top = Math.Clamp(snapped.Top, bounds.Top, _original.Bottom - MinZonePixels);
            bottom = Math.Clamp(snapped.Bottom, _original.Top + MinZonePixels, bounds.Bottom);
            if (!_action.HasFlag(Edges.Left)) left = _original.Left;
            if (!_action.HasFlag(Edges.Right)) right = _original.Right;
            if (!_action.HasFlag(Edges.Top)) top = _original.Top;
            if (!_action.HasFlag(Edges.Bottom)) bottom = _original.Bottom;
            next = new PixelRect(left, top, right, bottom);
        }
        Zones[Selected].Rect = next;
        Raise();
    }

    public void Release()
    {
        if (Drawing is { } drawn)
        {
            Drawing = null;
            _drawingArea = -1;
            if (drawn.Width >= MinZonePixels && drawn.Height >= MinZonePixels)
            {
                Zones.Add(new EditZone(NewId(), drawn));
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

    /// <summary>
    /// Replaces the selected zone by its two halves. The first half keeps the zone's id (and so its windows and
    /// number) and stays selected.
    /// </summary>
    public bool SplitSelected(SplitOrientation orientation)
    {
        if (Selected < 0) return false;
        var zone = Zones[Selected];
        if ((orientation == SplitOrientation.Vertical ? zone.Rect.Width : zone.Rect.Height) < 2 * MinZonePixels) return false;
        var (first, second) = Split(zone.Rect, orientation);
        zone.Rect = first;
        Zones.Insert(Selected + 1, new EditZone(NewId(), second));
        Raise();
        return true;
    }

    public void DeleteAll()
    {
        Zones.Clear();
        Selected = -1;
        Raise();
    }

    /// <summary>Index of the work area containing <paramref name="p"/>, or -1.</summary>
    public int AreaAt(PixelPoint p)
    {
        for (var i = 0; i < WorkAreas.Count; i++)
            if (WorkAreas[i].Contains(p)) return i;
        return -1;
    }

    private PixelRect SnapTo(PixelRect rect, Edges edges, int except)
    {
        var (xs, ys) = SnapLines(Zones.Where((_, i) => i != except).Select(z => z.Rect), WorkAreas);
        return Snap(rect, edges, xs, ys, _snap);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
