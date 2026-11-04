using System.Globalization;
using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>
/// What the Android "Goal" widget shows: one savings goal's photo lit up to <see cref="Share"/>, the percentage and, when
/// wide enough, the money. Platform-free, so it is tested here; the Android side only draws it.
/// </summary>
public sealed record GoalWidgetModel(
    string Id,
    string Name,
    double Share,
    string PercentText,
    string SavedShort,
    string PriceShort,
    string LeftShort,
    string PaceShort,
    bool IsReady,
    BlobRef? Photo,
    int Color)
{
    /// <summary>"4.35M of 13M".</summary>
    public string OfText => $"{SavedShort} of {PriceShort}";

    /// <summary>What a screen reader says for the widget.</summary>
    public string Description => IsReady
        ? $"{Name}: enough saved, {SavedShort}. Tap to record the purchase."
        : $"{Name}: {PercentText} saved, {OfText}, {LeftShort}. Tap to set money aside.";

    /// <summary>The goal the user put on the widget, else the first one being saved for; null when nothing is saved for.</summary>
    public static GoalWidgetModel? Build(GoalBook book, string? goalId, DateTimeOffset now, TimeZoneInfo zone)
    {
        var saving = book.Saving();
        var item = saving.FirstOrDefault(g => g.Id == goalId) ?? saving.FirstOrDefault();
        if (item is null) return null;
        var g = item.Value;
        var today = WalletFormat.LocalDay(now, zone);
        var pace = GoalMath.PerWeek(g, today) is { } week ? $"~{WalletFormat.Short(week)}/week" : "";
        if (pace.Length > 0 && g.Due is { } due) pace += " · by " + due.ToString("d MMM", CultureInfo.CurrentCulture);
        return new GoalWidgetModel(item.Id, g.Name, g.Share, GoalMath.Percent(g), WalletFormat.Short(g.Saved), WalletFormat.Short(g.Price),
            g.IsReady ? "Enough saved" : $"{WalletFormat.Short(g.Left)} to go", pace, g.IsReady, g.Photo, g.Color);
    }
}
