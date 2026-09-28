namespace Helm.Core.Hotkeys;

/// <summary>A shortcut that cannot work as configured.</summary>
/// <param name="Gesture">The contested key combination.</param>
/// <param name="Definitions">Helm hotkeys using it (two or more for an internal clash, one when another app owns it).</param>
/// <param name="UsedByAnotherApp">True when Windows refused the registration because something else owns the combination.</param>
public sealed record HotkeyConflict(HotkeyGesture Gesture, IReadOnlyList<HotkeyDefinition> Definitions, bool UsedByAnotherApp)
{
    public string Describe() => UsedByAnotherApp
        ? $"{Gesture} ({Definitions[0].Description}) is already used by another application."
        : $"{Gesture} is assigned to: {string.Join(", ", Definitions.Select(d => d.Description))}.";
}

public static class HotkeyConflictDetector
{
    /// <summary>
    /// Finds clashes among <paramref name="definitions"/> (same gesture used twice) and definitions Windows refused
    /// (<paramref name="failedKeys"/>, matched by <see cref="HotkeyDefinition.Key"/>) that are not explained by a clash.
    /// </summary>
    public static IReadOnlyList<HotkeyConflict> Detect(IEnumerable<HotkeyDefinition> definitions, IReadOnlySet<string>? failedKeys = null)
    {
        var active = definitions.Where(d => !d.Gesture.IsEmpty).ToList();
        var conflicts = new List<HotkeyConflict>();
        var clashing = new HashSet<string>();

        foreach (var group in active.GroupBy(d => d.Gesture))
        {
            var defs = group.ToList();
            if (defs.Count < 2) continue;
            conflicts.Add(new HotkeyConflict(group.Key, defs, UsedByAnotherApp: false));
            foreach (var d in defs) clashing.Add(d.Key);
        }

        if (failedKeys is not null)
        {
            foreach (var d in active)
            {
                if (failedKeys.Contains(d.Key) && !clashing.Contains(d.Key))
                    conflicts.Add(new HotkeyConflict(d.Gesture, [d], UsedByAnotherApp: true));
            }
        }

        return conflicts;
    }
}
