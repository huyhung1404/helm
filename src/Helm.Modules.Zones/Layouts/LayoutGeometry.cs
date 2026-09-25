using Helm.Core.Desktop;
using Helm.Core.Geometry;

namespace Helm.Modules.Zones.Layouts;

/// <summary>Pure geometry for custom layouts: reference rectangles, fraction/pixel conversion, edge snapping, splitting.</summary>
public static class LayoutGeometry
{
    public const int MinZonePixels = 40;

    /// <summary>
    /// The rectangle the layout's fractions refer to when shown on <paramref name="target"/>: that monitor's work area
    /// (Monitor scope) or the union of the work areas of the layout's monitors that are connected (Span scope).
    /// Null when a spanning layout has none of its monitors connected.
    /// </summary>
    public static PixelRect? ReferenceRect(ZoneLayout layout, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        if (layout.Scope == LayoutScope.Monitor) return target.WorkArea;
        PixelRect? union = null;
        foreach (var m in CoveredMonitors(layout, target, monitors))
            union = union is { } u ? u.Union(m.WorkArea) : m.WorkArea;
        return union;
    }

    /// <summary>The monitors a layout occupies when applied from <paramref name="target"/>.</summary>
    public static IReadOnlyList<MonitorInfo> CoveredMonitors(ZoneLayout layout, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors) =>
        layout.Scope == LayoutScope.Monitor
            ? [target]
            : monitors.Where(m => layout.MonitorIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<PixelRect> ToPixels(IReadOnlyList<ZoneRect> zones, PixelRect reference) =>
        ZoneMath.ToPixels(zones, reference, spacing: 0, applySpacing: false);

    public static ZoneRect FromPixels(PixelRect zone, PixelRect reference) => new(
        (zone.Left - reference.Left) / (double)reference.Width,
        (zone.Top - reference.Top) / (double)reference.Height,
        zone.Width / (double)reference.Width,
        zone.Height / (double)reference.Height);

    /// <summary>Keeps a zone inside <paramref name="bounds"/> and at least <see cref="MinZonePixels"/> in each dimension.</summary>
    public static PixelRect Clamp(PixelRect zone, PixelRect bounds)
    {
        var w = Math.Clamp(zone.Width, Math.Min(MinZonePixels, bounds.Width), bounds.Width);
        var h = Math.Clamp(zone.Height, Math.Min(MinZonePixels, bounds.Height), bounds.Height);
        var left = Math.Clamp(zone.Left, bounds.Left, bounds.Right - w);
        var top = Math.Clamp(zone.Top, bounds.Top, bounds.Bottom - h);
        return PixelRect.FromSize(left, top, w, h);
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

    /// <summary>A two-column starter layout so zones work before the user draws their own.</summary>
    public static ZoneLayout Starter(string? monitorId) => new()
    {
        Name = "Layout 1",
        Number = 1,
        Scope = LayoutScope.Monitor,
        MonitorIds = monitorId is null ? [] : [monitorId],
        Zones = [new ZoneRect(0, 0, 0.5, 1), new ZoneRect(0.5, 0, 0.5, 1)],
    };

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
