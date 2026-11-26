namespace Helm.Core.Geometry;

/// <summary>A point in physical (per-monitor DPI aware) screen pixels.</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>
/// An axis-aligned rectangle in physical screen pixels, stored as edges (Win32 RECT semantics:
/// <see cref="Right"/> and <see cref="Bottom"/> are exclusive).
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromSize(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool Contains(PixelRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect r) => r.Left < Right && r.Right > Left && r.Top < Bottom && r.Bottom > Top;

    public PixelRect Union(PixelRect r) =>
        new(Math.Min(Left, r.Left), Math.Min(Top, r.Top), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));

    public PixelRect Inflate(int dx, int dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>Distance from <paramref name="p"/> to the nearest point of this rectangle (0 when inside).</summary>
    public double DistanceTo(PixelPoint p)
    {
        int dx = Math.Max(Math.Max(Left - p.X, 0), p.X - (Right - 1));
        int dy = Math.Max(Math.Max(Top - p.Y, 0), p.Y - (Bottom - 1));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}
