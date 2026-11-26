using Helm.Core.Geometry;

namespace Helm.Modules.WindowZones.Model;

public enum SplitOrientation
{
    /// <summary>A vertical line: left and right halves.</summary>
    Vertical,

    /// <summary>A horizontal line: top and bottom halves.</summary>
    Horizontal,
}

/// <summary>Pure geometry: fractions ↔ pixels, hit testing, edge snapping and splitting for the editor.</summary>
public static class ZoneGeometry
{
    public const int MinZonePixels = 40;

    /// <summary>
    /// How far (physical pixels) a window may sit from its zone's top-left corner and still count as in the zone.
    /// Generous on purpose: some apps round their size, or refuse to be smaller than the zone.
    /// </summary>
    public const int InZoneTolerance = 16;

    /// <summary>A zone's pixel rectangle on a monitor whose work area is <paramref name="workArea"/>.</summary>
    public static PixelRect ToPixels(ZoneRect zone, PixelRect workArea)
    {
        var left = workArea.Left + (int)Math.Round(zone.X * workArea.Width);
        var top = workArea.Top + (int)Math.Round(zone.Y * workArea.Height);
        var right = workArea.Left + (int)Math.Round(zone.Right * workArea.Width);
        var bottom = workArea.Top + (int)Math.Round(zone.Bottom * workArea.Height);
        return new PixelRect(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    public static ZoneRect FromPixels(PixelRect zone, PixelRect workArea) => new(
        (zone.Left - workArea.Left) / (double)workArea.Width,
        (zone.Top - workArea.Top) / (double)workArea.Height,
        zone.Width / (double)workArea.Width,
        zone.Height / (double)workArea.Height);

    /// <summary>
    /// The zone under the cursor: the smallest one containing it (zones may overlap), otherwise the nearest within
    /// <paramref name="sensitivity"/> pixels (the cursor is in a gap). -1 when none.
    /// </summary>
    public static int HitTest(IReadOnlyList<PixelRect> zones, PixelPoint cursor, int sensitivity)
    {
        var best = -1;
        long bestArea = long.MaxValue;
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].Contains(cursor) && zones[i].Area < bestArea)
            {
                best = i;
                bestArea = zones[i].Area;
            }
        }
        if (best >= 0) return best;

        var bestDistance = double.MaxValue;
        for (var i = 0; i < zones.Count; i++)
        {
            var d = zones[i].DistanceTo(cursor);
            if (d <= sensitivity && d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>True while a window's visible frame still sits where its zone put it (see <see cref="InZoneTolerance"/>).</summary>
    public static bool IsInZone(PixelRect frame, PixelRect zone) =>
        Math.Abs(frame.Left - zone.Left) <= InZoneTolerance && Math.Abs(frame.Top - zone.Top) <= InZoneTolerance;

    /// <summary>
    /// The window to switch to among the windows of one zone, <paramref name="delta"/> steps from
    /// <paramref name="current"/>, wrapping around. <paramref name="windows"/> must keep a stable order so repeated
    /// presses visit every window. 0 when there is nothing to switch to.
    /// </summary>
    public static nint CycleTarget(IReadOnlyList<nint> windows, nint current, int delta)
    {
        if (windows.Count < 2) return 0;
        var index = -1;
        for (var i = 0; i < windows.Count; i++)
            if (windows[i] == current) { index = i; break; }
        if (index < 0) return windows[0];
        return windows[((index + delta) % windows.Count + windows.Count) % windows.Count];
    }

    /// <summary>Keeps a zone inside <paramref name="bounds"/> and at least <see cref="MinZonePixels"/> in each dimension.</summary>
    public static PixelRect Clamp(PixelRect zone, PixelRect bounds)
    {
        var w = Math.Clamp(zone.Width, Math.Min(MinZonePixels, bounds.Width), bounds.Width);
        var h = Math.Clamp(zone.Height, Math.Min(MinZonePixels, bounds.Height), bounds.Height);
        var left = Math.Clamp(zone.Left, bounds.Left, bounds.Right - w);
        var top = Math.Clamp(zone.Top, bounds.Top, bounds.Bottom - h);
        return PixelRect.FromSize(left, top, w, h);
    }

    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        return new PixelRect(left, top, Math.Max(left, Math.Min(a.Right, b.Right)), Math.Max(top, Math.Min(a.Bottom, b.Bottom)));
    }

    /// <summary>Of <paramref name="areas"/>, the one overlapping <paramref name="rect"/> the most (-1 when none does).</summary>
    public static int MostOverlapping(IReadOnlyList<PixelRect> areas, PixelRect rect)
    {
        var best = -1;
        long bestArea = 0;
        for (var i = 0; i < areas.Count; i++)
        {
            var area = Intersect(areas[i], rect).Area;
            if (area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }
        return best;
    }

    [Flags]
    public enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        Move = Left | Top | Right | Bottom,
    }

    /// <summary>
    /// Magnet: pulls the <paramref name="moving"/> edges of <paramref name="rect"/> onto the nearest candidate line
    /// within <paramref name="threshold"/> pixels. Moving the whole rectangle (<see cref="Edges.Move"/>) keeps its
    /// size and shifts it by the best horizontal and vertical match of either side.
    /// </summary>
    public static PixelRect Snap(PixelRect rect, Edges moving, IReadOnlyCollection<int> xLines, IReadOnlyCollection<int> yLines, int threshold)
    {
        if (moving == Edges.Move)
        {
            var dx = Best(xLines, threshold, rect.Left, rect.Right);
            var dy = Best(yLines, threshold, rect.Top, rect.Bottom);
            return rect.Offset(dx, dy);
        }

        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
        if (moving.HasFlag(Edges.Left)) left += Best(xLines, threshold, left);
        if (moving.HasFlag(Edges.Right)) right += Best(xLines, threshold, right);
        if (moving.HasFlag(Edges.Top)) top += Best(yLines, threshold, top);
        if (moving.HasFlag(Edges.Bottom)) bottom += Best(yLines, threshold, bottom);
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>Candidate lines for snapping: edges of the other zones and of every monitor work area.</summary>
    public static (List<int> X, List<int> Y) SnapLines(IEnumerable<PixelRect> others, IEnumerable<PixelRect> workAreas)
    {
        var xs = new List<int>();
        var ys = new List<int>();
        foreach (var r in others.Concat(workAreas))
        {
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }
        return (xs.Distinct().ToList(), ys.Distinct().ToList());
    }

    /// <summary>Splits a zone into two halves: side by side (vertical line) or stacked (horizontal line).</summary>
    public static (PixelRect First, PixelRect Second) Split(PixelRect zone, SplitOrientation orientation)
    {
        if (orientation == SplitOrientation.Vertical)
        {
            var mid = zone.Left + zone.Width / 2;
            return (new PixelRect(zone.Left, zone.Top, mid, zone.Bottom), new PixelRect(mid, zone.Top, zone.Right, zone.Bottom));
        }
        var center = zone.Top + zone.Height / 2;
        return (new PixelRect(zone.Left, zone.Top, zone.Right, center), new PixelRect(zone.Left, center, zone.Right, zone.Bottom));
    }

    private static int Best(IReadOnlyCollection<int> lines, int threshold, params int[] values)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        foreach (var value in values)
        {
            foreach (var line in lines)
            {
                var d = line - value;
                if (Math.Abs(d) <= threshold && Math.Abs(d) < bestDistance)
                {
                    best = d;
                    bestDistance = Math.Abs(d);
                }
            }
        }
        return best;
    }
}
