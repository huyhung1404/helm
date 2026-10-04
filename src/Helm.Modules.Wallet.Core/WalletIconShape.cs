namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet icon, drawn the same on both platforms (each builds its own vector image from this): a wallet with a card
/// peeking out of it and a clasp on the right, in an emerald-to-teal gradient, with no background. Path data is in a
/// 100 × 100 box (WPF/Avalonia syntax); with its stroke it spans the box like the Claude symbol, so every tool icon
/// looks the same size.
/// </summary>
public static class WalletIconShape
{
    public const double Size = 100;

    /// <summary>The card behind the wallet, filled.</summary>
    public const string Card = "M16 27L64 6Q71 3 74 10L81 27Z";

    /// <summary>The wallet's body, stroked with <see cref="Stroke"/>.</summary>
    public const string Body = "M18.5 27.5H81.5A14 14 0 0 1 95.5 41.5V80.5A14 14 0 0 1 81.5 94.5H18.5A14 14 0 0 1 4.5 80.5V41.5A14 14 0 0 1 18.5 27.5Z";

    /// <summary>The clasp, filled, with a round hole (even-odd).</summary>
    public const string Clasp = "M68 51H96V71H68A10 10 0 0 1 68 51Z M71 61m-4 0a4 4 0 1 0 8 0a4 4 0 1 0 -8 0";

    public const double Stroke = 9;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF047857;

    public const uint GradientEnd = 0xFF2DD4BF;
}
