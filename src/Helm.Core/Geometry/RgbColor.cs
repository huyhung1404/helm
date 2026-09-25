using System.Globalization;

namespace Helm.Core.Geometry;

/// <summary>A plain 24-bit color that can be used from non-WPF code (GDI overlays) and round-trips as "#RRGGBB".</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Parse(string? hex, RgbColor fallback)
    {
        if (TryParse(hex, out var c)) return c;
        return fallback;
    }

    public static bool TryParse(string? hex, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // #AARRGGBB -> ignore alpha
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public RgbColor Lerp(RgbColor other, double t) => new(
        (byte)Math.Round(R + (other.R - R) * t),
        (byte)Math.Round(G + (other.G - G) * t),
        (byte)Math.Round(B + (other.B - B) * t));

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();
}
