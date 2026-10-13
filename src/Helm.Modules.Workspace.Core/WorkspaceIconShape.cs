namespace Helm.Modules.Workspace;

/// <summary>
/// The Workspace Controller icon: a window with a prompt in it (a chevron and a cursor), in an orange-to-pink
/// gradient, with no background. Path data is in a 100 × 100 box and the window fills it edge to edge, like the other
/// tool icons. Shared, so the phone draws the same icon.
/// </summary>
public static class WorkspaceIconShape
{
    public const double Size = 100;

    /// <summary>The window, stroked with <see cref="Stroke"/>.</summary>
    public const string Frame = "M20 12H80A16 16 0 0 1 96 28V72A16 16 0 0 1 80 88H20A16 16 0 0 1 4 72V28A16 16 0 0 1 20 12Z";

    /// <summary>The prompt chevron, stroked with <see cref="Stroke"/> and round ends.</summary>
    public const string Chevron = "M26 37L43 50L26 63";

    /// <summary>The cursor after the prompt, filled.</summary>
    public const string Cursor = "M52 57H72A5 5 0 0 1 72 67H52A5 5 0 0 1 52 57Z";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFF97316;

    public const uint GradientEnd = 0xFFDB2777;
}
