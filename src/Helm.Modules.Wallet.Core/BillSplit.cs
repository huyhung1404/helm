namespace Helm.Modules.Wallet;

/// <summary>A bill shared out evenly: what each of the others pays, and what is left for the user.</summary>
public static class BillSplit
{
    /// <summary>
    /// <paramref name="total"/> shared by <paramref name="people"/> (the user among them). Each share is a round
    /// thousand when the shares are large enough; the user's share takes what rounding leaves, so it all adds up.
    /// </summary>
    public static (decimal Each, decimal Mine) Shares(decimal total, int people)
    {
        if (people < 2) throw new ArgumentOutOfRangeException(nameof(people), "A split needs at least two people.");
        var each = total / people;
        each = each >= 10_000 ? Math.Round(each / 1000, MidpointRounding.AwayFromZero) * 1000 : Math.Round(each, 0, MidpointRounding.AwayFromZero);
        return (each, total - each * (people - 1));
    }
}
