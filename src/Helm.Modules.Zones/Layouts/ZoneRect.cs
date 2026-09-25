namespace Helm.Modules.Zones.Layouts;

/// <summary>A zone as fractions (0..1) of the monitor work area, so layouts scale across resolutions and DPI.</summary>
public readonly record struct ZoneRect(double X, double Y, double Width, double Height)
{
    public const double Epsilon = 1e-6;

    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>Clamps into the unit square keeping at least <paramref name="minSize"/> in each dimension.</summary>
    public ZoneRect Clamp(double minSize = 0.02)
    {
        var w = Math.Clamp(Width, minSize, 1);
        var h = Math.Clamp(Height, minSize, 1);
        var x = Math.Clamp(X, 0, 1 - w);
        var y = Math.Clamp(Y, 0, 1 - h);
        return new ZoneRect(x, y, w, h);
    }

    public static ZoneRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}
