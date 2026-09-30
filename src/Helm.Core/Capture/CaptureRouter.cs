namespace Helm.Core.Capture;

/// <summary>
/// Picks the target for a captured text: "/t buy milk" goes to the target whose prefix is "t" (the prefix is removed),
/// anything else to the selected target. Shared by the Windows capture window and the Android share sheet.
/// </summary>
public static class CaptureRouter
{
    /// <summary>Targets of the tools that are on, in display order.</summary>
    public static IReadOnlyList<ICaptureTarget> Available(IEnumerable<ICaptureTarget> targets, Func<string, bool> isModuleEnabled) =>
        targets.Where(t => isModuleEnabled(t.ModuleId)).OrderBy(t => t.Order).ThenBy(t => t.Name, StringComparer.CurrentCulture).ToList();

    /// <summary>
    /// The target a "/x " prefix at the start of <paramref name="text"/> asks for, and the text without it; null (and the
    /// text unchanged) when there is no known prefix.
    /// </summary>
    public static (ICaptureTarget? Target, string Text) SplitPrefix(IReadOnlyList<ICaptureTarget> targets, string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/') return (null, text);
        var end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        var prefix = trimmed[1..end];
        var target = targets.FirstOrDefault(t => string.Equals(t.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        return target is null ? (null, text) : (target, trimmed[end..].TrimStart());
    }

    /// <summary>
    /// The target to select first for shared text: the first one that claims it (a video link → Watch Later), else
    /// <paramref name="fallback"/>.
    /// </summary>
    public static ICaptureTarget? Suggest(IReadOnlyList<ICaptureTarget> targets, string text, ICaptureTarget? fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        foreach (var target in targets)
        {
            try
            {
                if (target.Claims(text)) return target;
            }
            catch (Exception)
            {
                // A target must never break the capture box.
            }
        }
        return fallback;
    }

    /// <summary>The target for <paramref name="text"/>: its prefix, else <paramref name="selected"/>.</summary>
    public static (ICaptureTarget? Target, string Text) Resolve(IReadOnlyList<ICaptureTarget> targets, ICaptureTarget? selected, string text)
    {
        var (prefixed, rest) = SplitPrefix(targets, text);
        return prefixed is not null ? (prefixed, rest) : (selected, text);
    }
}
