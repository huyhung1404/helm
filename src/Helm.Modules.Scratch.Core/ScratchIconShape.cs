namespace Helm.Modules.Scratch;

/// <summary>
/// The Scratch icon, drawn the same on both platforms (each builds its own vector image from this): a sticky note with
/// its bottom-right corner peeling and a quick scribble on it, in a coral gradient (rose to peach), with no background.
/// Path data is in a 100 × 100 box (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol, so every
/// tool icon looks the same size.
/// </summary>
public static class ScratchIconShape
{
    public const double Size = 100;

    /// <summary>The note's outline, the bottom-right corner cut where it peels.</summary>
    public const string Note = "M12 5H88A7 7 0 0 1 95 12V64L64 95H12A7 7 0 0 1 5 88V12A7 7 0 0 1 12 5Z";

    /// <summary>The peeling corner.</summary>
    public const string Peel = "M95 64H71A7 7 0 0 0 64 71V95";

    /// <summary>A wavy line and a short one: something jotted down in a hurry. Round caps.</summary>
    public const string Scribble = "M25 34C31 24 37 44 43 34S55 24 61 34S71 42 75 34M25 56C30 48 35 62 40 54";

    /// <summary>Every part is stroked with this width (about the same ink as the other tools' icons).</summary>
    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFE11D48;

    public const uint GradientEnd = 0xFFFDBA74;
}
