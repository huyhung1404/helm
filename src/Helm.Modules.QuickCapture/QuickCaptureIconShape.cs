namespace Helm.Modules.QuickCapture;

/// <summary>
/// The Quick Capture icon, drawn the same on both platforms (each builds its own vector image from this): a lightning
/// bolt in an amber-to-yellow gradient, with no background. Path data is in a 100 × 100 box and its height fills it
/// (1 to 99), like the Claude symbol, so every tool icon looks the same size. Plain constants: the Android project
/// compiles this same file (linked), as there is no shared Quick Capture project.
/// </summary>
public static class QuickCaptureIconShape
{
    public const double Size = 100;

    /// <summary>The bolt, filled with the gradient and stroked with <see cref="Stroke"/> and round joins (soft corners).</summary>
    public const string Bolt = "M62 5L14 58H46L38 95L86 40H54Z";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFF59E0B;

    public const uint GradientEnd = 0xFFFDE047;
}
