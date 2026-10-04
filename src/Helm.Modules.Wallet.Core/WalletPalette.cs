namespace Helm.Modules.Wallet;

/// <summary>
/// The colours of Wallet's categories and charts (ARGB), the same on both platforms. The eight category colours are a
/// colour-blind-checked categorical palette, stepped for the light and the dark card surfaces (#FBFBFB, #2B2B2B);
/// adjacent slots stay apart for every kind of colour blindness. A colour follows its category everywhere (never its
/// rank), and the charts always print the category's name and amount beside it, so colour never carries meaning
/// alone. Slot 0 is the neutral grey of "Other" and of transactions still to categorize.
/// </summary>
public static class WalletPalette
{
    public const int Slots = 8;

    // Index 0: neutral grey; 1–8: blue, orange, aqua, yellow, magenta, green, violet, red.
    private static readonly uint[] s_light =
        [0xFF898781, 0xFF2A78D6, 0xFFEB6834, 0xFF1BAF7A, 0xFFEDA100, 0xFFE87BA4, 0xFF008300, 0xFF4A3AA7, 0xFFE34948];

    private static readonly uint[] s_dark =
        [0xFF898781, 0xFF3987E5, 0xFFD95926, 0xFF199E70, 0xFFC98500, 0xFFD55181, 0xFF008300, 0xFF9085E9, 0xFFE66767];

    /// <summary>A category's colour (out-of-range slots are grey).</summary>
    public static uint Color(int slot, bool dark) => (dark ? s_dark : s_light)[slot is >= 0 and <= Slots ? slot : 0];

    /// <summary>The same colour with an alpha (0–255), for the soft disc behind a category's icon.</summary>
    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>The bars of a single-series chart (the accent of the charts).</summary>
    public static uint Accent(bool dark) => Color(1, dark);

    /// <summary>A bar under the pointer.</summary>
    public static uint AccentStrong(bool dark) => dark ? 0xFF6DA7EC : 0xFF1C5CAB;

    /// <summary>The bars that are context, not the point (the other months).</summary>
    public static uint Muted(bool dark) => dark ? 0xFF5F5E59 : 0xFFC3C2B7;

    /// <summary>The empty part of the budget meter: a light step of the accent's own ramp.</summary>
    public static uint Track(bool dark) => dark ? 0xFF1F3A5F : 0xFFCDE2FB;

    /// <summary>Over budget (shown with the words "Over budget", never colour alone).</summary>
    public static uint Critical => 0xFFD03B3B;

    /// <summary>Hairline gridlines and the baseline.</summary>
    public static uint Grid(bool dark) => dark ? 0xFF3D3D3A : 0xFFE1E0D9;

    public static uint Baseline(bool dark) => dark ? 0xFF5A5A55 : 0xFFC3C2B7;

    /// <summary>"Other" in the donut: lighter than the grey of the transactions still to categorize.</summary>
    public static uint Other(bool dark) => dark ? 0xFF5F5E59 : 0xFFC3C2B7;

    /// <summary>The slot a new category gets: the one its kind uses least.</summary>
    public static int NextSlot(IEnumerable<int> used)
    {
        var counts = new int[Slots + 1];
        foreach (var slot in used)
            if (slot is >= 1 and <= Slots) counts[slot]++;
        var best = 1;
        for (var s = 2; s <= Slots; s++)
            if (counts[s] < counts[best]) best = s;
        return best;
    }
}
