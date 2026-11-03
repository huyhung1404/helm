namespace Helm.Ai.Assistant;

/// <summary>
/// Ask Helm's "photo to data": how big a photo is sent, which models read one, the tool groups a photo needs and what
/// the model is asked to do with it. A photo is read by a Helm PC's agent that reads pictures when one answers (it
/// travels as a blob of its own, <see cref="IAgentJobPhotos"/>), else through the API on the picked model.
/// </summary>
public static class AssistantPhotos
{
    /// <summary>The longer side a photo is sent at: Claude scales anything larger down to this anyway.</summary>
    public const int MaxSide = 1568;

    public const int JpegQuality = 85;

    /// <summary>A receipt or a transfer is money, a whiteboard is a note and tasks, whatever the words say.</summary>
    public static readonly IReadOnlyList<string> Groups = ["wallet", "tracker", "notes"];

    /// <summary>
    /// The power of two a photo of this size is decoded at: the smallest picture still at least <see cref="MaxSide"/> on
    /// its longer side, so a 50 MP photo never has to fit in memory whole.
    /// </summary>
    public static int SampleSize(int width, int height)
    {
        var side = Math.Max(width, height);
        var sample = 1;
        while (side / (sample * 2) >= MaxSide) sample *= 2;
        return sample;
    }

    /// <summary>The size a decoded picture is sent at: at most <see cref="MaxSide"/> on its longer side, never enlarged.</summary>
    public static (int Width, int Height) Fit(int width, int height)
    {
        var side = Math.Max(width, height);
        if (side <= MaxSide || side <= 0) return (width, height);
        var scale = (double)MaxSide / side;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Whether a model takes pictures. Every current Claude model does; of Grok's, the text-only lines do not (grok-3,
    /// the coding models, grok-2 without "vision"). An unknown one is tried, and the provider's no turns it down.
    /// </summary>
    public static bool Reads(AiProvider provider, string model)
    {
        if (provider != AiProviders.Grok) return true;
        var id = model.ToLowerInvariant();
        return !(id.StartsWith("grok-3", StringComparison.Ordinal) || id.StartsWith("grok-code", StringComparison.Ordinal)
            || id.StartsWith("grok-2", StringComparison.Ordinal) && !id.Contains("vision", StringComparison.Ordinal));
    }

    /// <summary>What the model is asked with the photo; <paramref name="said"/> is the user's sentence with it, if any.</summary>
    public static string Prompt(string said) => $"""
        The user sent a photo{(said.Length > 0 ? $" and said: \"{said}\"" : " without a word")}. Read it and save what it shows with Helm's tools, as if they had said it:
        - a receipt or a bill: one wallet_add_transaction with its total, the shop or what was bought as description, the category it fits and the time printed on it (else now); when they say whom they shared it with, wallet_split_bill instead (or wallet_add_debt for what one person owes);
        - a bank transfer screenshot: the transaction (money out or in, the other person in the description), and a debt when it lends money or pays one back;
        - a whiteboard, a page or a handwritten list: notes_create with its text, and tracker_add_tasks for the to-dos on it.
        Never guess an amount you cannot read. When the photo shows nothing to save, say in one sentence what it shows.
        """;
}
