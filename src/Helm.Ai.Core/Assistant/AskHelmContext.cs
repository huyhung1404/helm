namespace Helm.Ai.Assistant;

/// <summary>
/// What Ask Helm's card was opened about, when a tool opens it for one thing (Wallet's "What was it?" notification:
/// that transaction). The card shows <see cref="Title"/> as the question; the first turn carries <see cref="Prompt"/>
/// before the user's words, with the tools of <see cref="Groups"/> (see <see cref="ToolScope"/>).
/// </summary>
/// <param name="Title">The question the card shows ("−65.000 ₫ · Techcombank: khoản này là gì?").</param>
/// <param name="Prompt">For the model: what the user is answering and which tool fills it in.</param>
/// <param name="Groups">Tool groups the answer needs ("wallet"), whatever the user's words name.</param>
public sealed record AskHelmContext(string Title, string Prompt, IReadOnlyList<string> Groups)
{
    /// <summary>The card's activity (Quick Capture's project): a tool opens it by this name, without a reference.</summary>
    public const string ActivityName = "com.huyhung1404.helm.AskHelmActivity";

    public const string ExtraTitle = "com.huyhung1404.helm.extra.ASK_TITLE";
    public const string ExtraPrompt = "com.huyhung1404.helm.extra.ASK_PROMPT";

    /// <summary>Comma-separated <see cref="Groups"/>.</summary>
    public const string ExtraGroups = "com.huyhung1404.helm.extra.ASK_GROUPS";

    /// <summary>Int: a notification of Helm's that the card clears once the answer saved something.</summary>
    public const string ExtraNotification = "com.huyhung1404.helm.extra.ASK_NOTIFICATION";

    /// <summary>The first turn's text: the context, then what the user said.</summary>
    internal string Before(string said) => $"{Prompt.Trim()}\n\nThe user says: {said}";
}
