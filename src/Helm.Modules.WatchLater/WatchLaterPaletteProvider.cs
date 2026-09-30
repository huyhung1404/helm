using Helm.Core.Palette;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Watch Later in the command palette: saved videos whose title, channel or note match (Enter opens the video), and
/// "Watch later" for a pasted link. The shell navigation is resolved when a result runs (it depends on the module
/// list, which the palette module is part of).
/// </summary>
internal sealed class WatchLaterPaletteProvider(WatchLaterStore store, IProcessLauncher launcher, IServiceProvider services) : IPaletteProvider
{
    private const int MaxResults = 6;

    public string? ModuleId => WatchLaterIds.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        if (query.IsEmpty) yield break;

        // A link typed or pasted into the palette: save it.
        if (VideoLink.Find(query.Text) is { Source: not WatchSource.Other } link)
        {
            var saved = store.Find(link.Key);
            yield return new PaletteItem(saved is null ? "Watch later" : "Move to the top of Watch Later",
                WatchLaterFormat.KindName(link.Source, link.Kind), PaletteKind.Action, 1.0, () =>
                {
                    store.Add(link, VideoLink.TextAround(query.Text, link));
                    services.GetRequiredService<IShellNavigation>().ShowPage(typeof(WatchLaterContentPage));
                });
            yield break;
        }

        var openScore = Math.Max(query.Score("Watch Later"), query.Score("Xem sau"));
        if (openScore > 0)
            yield return new PaletteItem("Watch Later", "Open your saved videos", PaletteKind.Action, openScore,
                () => services.GetRequiredService<IShellNavigation>().ShowPage(typeof(WatchLaterContentPage)));

        foreach (var (id, item, score) in store.Items(WatchFilter.ToWatch)
                     .Select(i => (i.Id, i.Value, score: query.Score(i.Value.DisplayTitle, $"{i.Value.Channel} {i.Value.Note}")))
                     .Where(x => x.score > 0)
                     .OrderByDescending(x => x.score)
                     .Take(MaxResults))
        {
            var detail = string.Join(" · ", new[] { item.Channel, WatchLaterFormat.KindName(item.Source, item.Kind),
                item.DurationSeconds is int d && d > 0 ? WatchLaterFormat.Duration(d) : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            yield return new PaletteItem(item.DisplayTitle, detail, PaletteKind.Video, score, () => launcher.OpenUrl(item.Url));
        }
    }
}
