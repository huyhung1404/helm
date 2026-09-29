using Helm.Core.Palette;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.CommandPalette;

/// <summary>
/// Asks every provider of a tool that is on, and merges their results into one ranked list. Helm's own results come
/// first: results from outside Helm (<see cref="PaletteItem.IsExternal"/>) count for <see cref="ExternalWeight"/> of
/// their score, so an app or a file only wins over a note when it matches much better (e.g. the start of its name
/// against a word deep in the note's text).
/// </summary>
public static class PaletteSearch
{
    public const int MaxResults = 12;

    public const double ExternalWeight = 0.6;

    /// <summary>Kinds that win a tie: what you work on before where it lives.</summary>
    private static int KindRank(PaletteKind kind) => kind switch
    {
        PaletteKind.Action => 0,
        PaletteKind.Note => 1,
        PaletteKind.Task => 2,
        PaletteKind.Debt => 3,
        PaletteKind.VaultItem => 4,
        PaletteKind.Page => 5,
        PaletteKind.Setting => 6,
        PaletteKind.App => 7,
        PaletteKind.Window => 8,
        PaletteKind.WindowsSetting => 9,
        PaletteKind.Folder => 10,
        PaletteKind.File => 11,
        PaletteKind.Web => 12,
        _ => 13,
    };

    /// <summary>The score results are ranked by: <see cref="ExternalWeight"/> applied to results from outside Helm.</summary>
    public static double Rank(PaletteItem item) => item.IsExternal ? item.Score * ExternalWeight : item.Score;

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
        return Order(all, max);
    }

    /// <summary>The slow providers together; a provider that fails or is cancelled adds nothing.</summary>
    public static async Task<IReadOnlyList<PaletteItem>> SearchSlowAsync(IEnumerable<ISlowPaletteProvider> providers, Func<string, bool> isModuleEnabled,
        PaletteQuery query, CancellationToken ct, ILogger? logger = null)
    {
        var tasks = providers.Where(p => p.ModuleId is not { } id || isModuleEnabled(id)).Select(async p =>
        {
            try
            {
                return await p.SearchAsync(query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (IReadOnlyList<PaletteItem>)[];
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Palette provider {Provider} failed", p.GetType().Name);
                return [];
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).Where(i => i.Score > 0).ToList();
    }

    /// <summary>Best first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<PaletteItem> Order(IEnumerable<PaletteItem> items, int max = MaxResults) =>
        items
            .OrderByDescending(i => Math.Round(Rank(i), 3))
            .ThenBy(i => i.IsExternal)
            .ThenBy(i => KindRank(i.Kind))
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
}
