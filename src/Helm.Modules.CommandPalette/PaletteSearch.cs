using Helm.Core.Palette;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.CommandPalette;

/// <summary>Asks every provider of a tool that is on, and merges their results into one ranked list.</summary>
public static class PaletteSearch
{
    public const int MaxResults = 12;

    /// <summary>Kinds that win a tie: what you work on before where it lives.</summary>
    private static int KindRank(PaletteKind kind) => kind switch
    {
        PaletteKind.Action => 0,
        PaletteKind.Note => 1,
        PaletteKind.Task => 2,
        PaletteKind.Debt => 3,
        PaletteKind.VaultItem => 4,
        PaletteKind.Page => 5,
        PaletteKind.App => 6,
        PaletteKind.Setting => 7,
        _ => 8,
    };

    public static IReadOnlyList<PaletteItem> Search(IEnumerable<IPaletteProvider> providers, Func<string, bool> isModuleEnabled,
        PaletteQuery query, ILogger? logger = null, int max = MaxResults)
    {
        var all = new List<PaletteItem>();
        foreach (var provider in providers)
        {
            if (provider.ModuleId is { } id && !isModuleEnabled(id)) continue;
            try
            {
                all.AddRange(provider.Search(query).Where(i => i.Score > 0));
            }
            catch (Exception ex)
            {
                // One broken source must not empty the palette.
                logger?.LogWarning(ex, "Palette provider {Provider} failed", provider.GetType().Name);
            }
        }
        return all
            .OrderByDescending(i => Math.Round(i.Score, 3))
            .ThenBy(i => KindRank(i.Kind))
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }
}
