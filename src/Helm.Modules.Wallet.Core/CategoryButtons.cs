namespace Helm.Modules.Wallet;

/// <summary>A category button of the phone's "What was it?" notification: one of the user's, or a new one AI proposed.</summary>
/// <param name="NewName">The category the tap creates (AI's proposal); null for an existing one.</param>
public sealed record CategoryButton(string Label, string? CategoryId, string? NewName, CategoryKind Kind);

public static class CategoryButtons
{
    /// <summary>The voice button of the "What was it?" notification, before the categories: opens Ask Helm listening.</summary>
    public const string SayLabel = "Nói";

    /// <summary>The categories the notification offers beside <see cref="SayLabel"/>: the two likeliest.</summary>
    public const int NotificationCount = 2;

    /// <summary>The notification's category buttons: the <see cref="NotificationCount"/> likeliest (<see cref="For"/>).</summary>
    public static IReadOnlyList<CategoryButton> ForNotification(IReadOnlyList<CategoryInfo> suggestions, CategoryProposal? proposal,
        Func<string, CategoryInfo?> category) => For(suggestions, proposal, category, NotificationCount);

    /// <summary>
    /// AI's proposal first (a new category marked "+", created only if tapped; one of the user's even when the history
    /// did not suggest it), then the history's suggestions, at most <paramref name="count"/>.
    /// </summary>
    public static IReadOnlyList<CategoryButton> For(IReadOnlyList<CategoryInfo> suggestions, CategoryProposal? proposal,
        Func<string, CategoryInfo?> category, int count)
    {
        var buttons = new List<CategoryButton>();
        if (proposal is { NewName: { } name }) buttons.Add(new CategoryButton("+ " + name, null, name, proposal.NewKind));
        else if (proposal?.CategoryId is { } picked && category(picked) is { } info) buttons.Add(new CategoryButton(info.Name, info.Id, null, info.Kind));
        foreach (var c in suggestions)
            if (buttons.All(b => b.CategoryId != c.Id)) buttons.Add(new CategoryButton(c.Name, c.Id, null, c.Kind));
        return buttons.Take(Math.Max(0, count)).ToList();
    }
}
