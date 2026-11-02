namespace Helm.Modules.Keyboard;

/// <summary>
/// The Helm Keyboard icon: a bare keyboard (four keys, three keys offset below them, a space bar), with no letter on it
/// since it types English, Vietnamese and Chinese alike, in a red-to-amber gradient with no background. Keys are in a
/// 100 × 100 box and the rows fill its width edge to edge, like the other tool icons. PC only, so the shape lives in the
/// PC project.
/// </summary>
public static class KeyboardIconShape
{
    public const double Size = 100;

    /// <summary>Side of one key.</summary>
    public const double Key = 16;

    /// <summary>Space between keys (4 × 16 + 3 × 12 = 100).</summary>
    public const double Gap = 12;

    public const double Radius = 4;

    /// <summary>Every key, filled with the gradient: the top row, the row below shifted half a key, the space bar.</summary>
    public static readonly (double X, double Y, double Width, double Height)[] Keys =
    [
        (0, 16, Key, Key), (28, 16, Key, Key), (56, 16, Key, Key), (84, 16, Key, Key),
        (14, 42, Key, Key), (42, 42, Key, Key), (70, 42, Key, Key),
        (14, 68, 72, Key),
    ];

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFEF4444;

    public const uint GradientEnd = 0xFFF59E0B;
}
