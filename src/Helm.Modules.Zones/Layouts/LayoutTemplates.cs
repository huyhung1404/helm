namespace Helm.Modules.Zones.Layouts;

/// <summary>Generates the built-in template layouts (Focus, Columns, Rows, Grid, Priority Grid).</summary>
public static class LayoutTemplates
{
    public const int MaxZones = 40;

    public static readonly LayoutKind[] Kinds = [LayoutKind.Focus, LayoutKind.Columns, LayoutKind.Rows, LayoutKind.Grid, LayoutKind.PriorityGrid];

    public static string TemplateId(LayoutKind kind) => $"template-{kind.ToString().ToLowerInvariant()}";

    public static string DisplayName(LayoutKind kind) => kind switch
    {
        LayoutKind.PriorityGrid => "Priority Grid",
        LayoutKind.CustomGrid => "Custom grid",
        LayoutKind.CustomCanvas => "Custom canvas",
        _ => kind.ToString(),
    };

    /// <summary>The template entries shipped in a fresh layouts.json.</summary>
    public static IEnumerable<LayoutDefinition> Defaults() => Kinds.Select(kind => new LayoutDefinition
    {
        Id = TemplateId(kind),
        Name = DisplayName(kind),
        Kind = kind,
        ZoneCount = kind switch
        {
            LayoutKind.Grid => 4,
            _ => 3,
        },
    });

    public static IReadOnlyList<ZoneRect> Generate(LayoutKind kind, int zoneCount) =>
        kind == LayoutKind.Focus ? Focus(zoneCount) : GenerateGrid(kind, zoneCount).ToZones();

    public static GridLayout GenerateGrid(LayoutKind kind, int zoneCount)
    {
        var n = Math.Clamp(zoneCount, 1, MaxZones);
        return kind switch
        {
            LayoutKind.Columns => GridLayout.Uniform(1, n, (_, c) => c),
            LayoutKind.Rows => GridLayout.Uniform(n, 1, (r, _) => r),
            LayoutKind.Grid => Grid(n),
            LayoutKind.PriorityGrid => PriorityGrid(n),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a grid template."),
        };
    }

    /// <summary>Overlapping cascaded zones, each 60% of the work area, stepping down-right.</summary>
    public static IReadOnlyList<ZoneRect> Focus(int zoneCount)
    {
        var n = Math.Clamp(zoneCount, 1, MaxZones);
        const double size = 0.6;
        var step = n > 1 ? Math.Min(0.05, (1 - size - 0.2) / (n - 1)) : 0;
        var origin = (1 - size - step * (n - 1)) / 2;
        return Enumerable.Range(0, n).Select(i => new ZoneRect(origin + i * step, origin + i * step, size, size)).ToList();
    }

    /// <summary>
    /// floor(√n) rows × ceil(n / rows) columns, filled in reading order; leftover cells of the last row are merged
    /// into the last zone.
    /// </summary>
    private static GridLayout Grid(int n)
    {
        var rows = (int)Math.Floor(Math.Sqrt(n));
        var columns = (int)Math.Ceiling(n / (double)rows);
        return GridLayout.Uniform(rows, columns, (r, c) => Math.Min(r * columns + c, n - 1));
    }

    /// <summary>
    /// A wide primary column in the middle with side columns stacked into rows:
    /// 1 → full; 2 → 2/3 + 1/3; n ≥ 3 → 25% | 50% | 25% with the side columns split into ⌊(n-1)/2⌋ and ⌈(n-1)/2⌉ rows.
    /// </summary>
    private static GridLayout PriorityGrid(int n)
    {
        if (n == 1) return GridLayout.Single();
        if (n == 2) return new GridLayout([1.0], [2.0 / 3, 1.0 / 3], [[0, 1]]);

        var left = (n - 1) / 2;
        var right = n - 1 - left;
        var rows = Lcm(left, right);
        var cells = new int[rows][];
        for (var r = 0; r < rows; r++)
        {
            var l = r / (rows / left);                 // zone within the left column
            var rr = r / (rows / right);               // zone within the right column
            cells[r] = [l, left, left + 1 + rr];      // left zones, then the center, then right zones
        }
        return new GridLayout(Enumerable.Repeat(1.0 / rows, rows), [0.25, 0.5, 0.25], cells).Normalize();
    }

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
