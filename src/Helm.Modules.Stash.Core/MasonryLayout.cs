namespace Helm.Modules.Stash;

/// <summary>The arithmetic of the card wall, shared by the WPF and Avalonia panels.</summary>
public static class MasonryLayout
{
    /// <summary>How many columns fit at <paramref name="minColumnWidth"/>, and how wide each is when they share the width.</summary>
    public static (int Columns, double ColumnWidth) Columns(double width, double minColumnWidth, double spacing)
    {
        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (minColumnWidth + spacing)));
        return (columns, Math.Max(0, (width - spacing * (columns - 1)) / columns));
    }

    /// <summary>The column a new card goes into: the shortest one, the leftmost on a tie.</summary>
    public static int Shortest(double[] heights)
    {
        var best = 0;
        for (var i = 1; i < heights.Length; i++)
            if (heights[i] < heights[best] - 0.5) best = i;
        return best;
    }
}
