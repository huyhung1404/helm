using Helm.Core.Geometry;

namespace Helm.Modules.Zones.Layouts;

/// <summary>Pure geometry used by the snapping engine: fractions → pixels, hit testing, multi-zone spans, navigation.</summary>
public static class ZoneMath
{
    /// <summary>
    /// Converts fractional zones to pixel rectangles inside <paramref name="workArea"/>. Edges are rounded from the
    /// same fractions so neighbours share exact edges; with spacing, edges on the work-area border are inset by the
    /// full spacing and inner edges by half of it, so every gap is exactly <paramref name="spacing"/> pixels.
    /// </summary>
    public static IReadOnlyList<PixelRect> ToPixels(IReadOnlyList<ZoneRect> zones, PixelRect workArea, int spacing, bool applySpacing = true)
    {
        var s = applySpacing ? Math.Max(0, spacing) : 0;
        var result = new List<PixelRect>(zones.Count);
        foreach (var z in zones)
        {
            var left = workArea.Left + (int)Math.Round(z.X * workArea.Width);
            var top = workArea.Top + (int)Math.Round(z.Y * workArea.Height);
            var right = workArea.Left + (int)Math.Round(z.Right * workArea.Width);
            var bottom = workArea.Top + (int)Math.Round(z.Bottom * workArea.Height);

            if (s > 0)
            {
                left += z.X <= ZoneRect.Epsilon ? s : s - s / 2;
                top += z.Y <= ZoneRect.Epsilon ? s : s - s / 2;
                right -= z.Right >= 1 - ZoneRect.Epsilon ? s : s / 2;
                bottom -= z.Bottom >= 1 - ZoneRect.Epsilon ? s : s / 2;
            }

            result.Add(new PixelRect(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom)));
        }
        return result;
    }

    /// <summary>Spacing in physical pixels for a monitor DPI (settings are expressed at 96 DPI).</summary>
    public static int ScaleSpacing(int spacing, uint dpi) => (int)Math.Round(spacing * dpi / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Zone under the cursor: the smallest zone containing it (canvas zones may overlap), otherwise the nearest zone
    /// within <paramref name="sensitivity"/> pixels (the cursor is in a gap). -1 when none.
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

    /// <summary>
    /// Zones selected by dragging from <paramref name="anchor"/> to <paramref name="current"/>: every zone overlapping
    /// the bounding box of both, grown until stable so the union stays rectangular. Returns sorted indices.
    /// </summary>
    public static IReadOnlyList<int> SelectSpan(IReadOnlyList<PixelRect> zones, int anchor, int current)
    {
        if (anchor < 0 || current < 0 || anchor >= zones.Count || current >= zones.Count) return [];
        if (anchor == current) return [anchor];

        var box = zones[anchor].Union(zones[current]);
        var selected = new SortedSet<int> { anchor, current };
        bool grew;
        do
        {
            grew = false;
            for (var i = 0; i < zones.Count; i++)
            {
                if (selected.Contains(i)) continue;
                var overlap = Intersect(box, zones[i]);
                // Ignore slivers touching only the spacing gap.
                if (overlap.Width > 2 && overlap.Height > 2)
                {
                    selected.Add(i);
                    box = box.Union(zones[i]);
                    grew = true;
                }
            }
        }
        while (grew);
        return selected.ToList();
    }

    public static PixelRect UnionOf(IReadOnlyList<PixelRect> zones, IEnumerable<int> indices)
    {
        PixelRect? union = null;
        foreach (var i in indices)
            union = union is { } u ? u.Union(zones[i]) : zones[i];
        return union ?? default;
    }

    /// <summary>Index reached by moving <paramref name="delta"/> zones from <paramref name="current"/> (wraps). Unsnapped: first/last.</summary>
    public static int Step(int count, int? current, int delta)
    {
        if (count <= 0) return -1;
        if (current is not { } c || c < 0 || c >= count) return delta >= 0 ? 0 : count - 1;
        return ((c + delta) % count + count) % count;
    }

    /// <summary>True when zones are stacked top-to-bottom (each spans nearly the full width), e.g. the Rows template.</summary>
    public static bool IsVerticalStack(IReadOnlyList<ZoneRect> zones) =>
        zones.Count > 1 && zones.All(z => z.Width >= 0.9);

    /// <summary>The zone whose rectangle best matches <paramref name="frame"/> (for "which zone is this window in?").</summary>
    public static int MatchZone(IReadOnlyList<PixelRect> zones, PixelRect frame, int tolerance = 8)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (Math.Abs(z.Left - frame.Left) <= tolerance && Math.Abs(z.Top - frame.Top) <= tolerance
                && Math.Abs(z.Right - frame.Right) <= tolerance && Math.Abs(z.Bottom - frame.Bottom) <= tolerance)
                return i;
        }
        return -1;
    }

    private static PixelRect Intersect(PixelRect a, PixelRect b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
}
