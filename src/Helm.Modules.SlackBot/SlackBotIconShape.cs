namespace Helm.Modules.SlackBot;

/// <summary>
/// The Slack Bot icon: a robot's head (a rounded outline with ears and an antenna, two eyes and a mouth) in silver with a
/// glowing cyan face and antenna (Lumi's colours), with no background. The silver darkens towards the bottom-left so the
/// outline still shows on a light background. Ears to ears it spans the 100 × 100 box edge to edge, like the other
/// tool icons. PC only, so the shape lives in the PC project.
/// </summary>
public static class SlackBotIconShape
{
    public const double Size = 100;

    public const double Stroke = 8;

    /// <summary>The head's outline (centre line): x, y, width, height, corner radius; the stroke adds 4 on each side.</summary>
    public static readonly (double X, double Y, double Width, double Height, double Radius) Head = (12, 34, 76, 58, 16);

    /// <summary>The antenna's stalk (centre line, round caps) and the ball on top (centre, radius).</summary>
    public static readonly (double X, double Top, double Bottom) Stalk = (50, 16, 30);

    public static readonly (double X, double Y, double R) Ball = (50, 11, 7);

    /// <summary>The ears, filled: x, y, width, height, corner radius.</summary>
    public static readonly (double X, double Y, double Width, double Height, double Radius)[] Ears =
        [(0, 53, 8, 20, 3), (92, 53, 8, 20, 3)];

    /// <summary>The eyes, filled: centre x, centre y, radius.</summary>
    public static readonly (double X, double Y, double R)[] Eyes = [(36, 60, 6.5), (64, 60, 6.5)];

    /// <summary>The mouth, filled: x, y, width, height, corner radius.</summary>
    public static readonly (double X, double Y, double Width, double Height, double Radius) Mouth = (40, 76, 20, 7, 3.5);

    /// <summary>The head's outline: a silver gradient from the bottom-left (darker) to the top-right (lighter), ARGB.</summary>
    public const uint HeadStart = 0xFF8A9AB2;

    public const uint HeadEnd = 0xFFE2E8F0;

    public const uint EarColor = 0xFF8A9AB2;

    /// <summary>The antenna and the face glow cyan.</summary>
    public const uint GlowColor = 0xFF38BDF8;
}
