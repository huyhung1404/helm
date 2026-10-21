namespace Helm.Modules.WindowZones.Model;

/// <summary>
/// Shortcut numbers stay with their zone: editing zones never renumbers the ones that were kept, so Ctrl+Alt+2 keeps
/// opening the same zone. New zones take the lowest free numbers in reading order (by monitor, then top to bottom,
/// left to right).
/// </summary>
public static class ZoneNumbers
{
    /// <summary>Gives every zone without a number (or with a taken or invalid one) the lowest free number, if any is left.</summary>
    public static void Assign(IList<ZoneDefinition> zones, IReadOnlyList<string> monitorOrder)
    {
        var used = new HashSet<int>();
        foreach (var z in zones)
        {
            if (z.Number is { } n && n is >= 1 and <= ZoneDefinition.MaxNumber && used.Add(n)) continue;
            z.Number = null;
        }

        var waiting = zones.Where(z => z.Number is null)
            .OrderBy(z => MonitorRank(monitorOrder, z.MonitorId))
            .ThenBy(z => Math.Round(z.Rect.Y, 2))
            .ThenBy(z => z.Rect.X);
        foreach (var zone in waiting)
        {
            var free = Enumerable.Range(1, ZoneDefinition.MaxNumber).FirstOrDefault(n => !used.Contains(n));
            if (free == 0) break;
            zone.Number = free;
            used.Add(free);
        }
    }

    /// <summary>Gives <paramref name="zoneId"/> the number <paramref name="number"/> (null: none); a zone that had it swaps.</summary>
    public static void Set(IList<ZoneDefinition> zones, string zoneId, int? number)
    {
        if (zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        if (number is { } n && n is < 1 or > ZoneDefinition.MaxNumber) return;
        if (number is not null && zones.FirstOrDefault(z => z.Id != zoneId && z.Number == number) is { } other)
            other.Number = zone.Number;
        zone.Number = number;
    }

    private static int MonitorRank(IReadOnlyList<string> order, string monitorId)
    {
        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], monitorId, StringComparison.OrdinalIgnoreCase)) return i;
        return order.Count;
    }
}
