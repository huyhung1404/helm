using Helm.Core.Palette;
using Helm.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker in the command palette: open tasks and the people in the debt book whose names match, and the tasks that
/// are overdue or due today when nothing is typed. Choosing one opens its workspace. The shell navigation is resolved
/// when a result runs (it depends on the module list, which the palette module is part of).
/// </summary>
internal sealed class TrackerPaletteProvider(TrackerStore store, TrackerViewModel viewModel, IServiceProvider services) : IPaletteProvider
{
    private const int MaxResults = 8;
    private const int Suggestions = 3;

    public string? ModuleId => TrackerIds.ModuleId;

    public IEnumerable<PaletteItem> Search(PaletteQuery query)
    {
        var now = store.Now;
        var workspaces = store.Workspaces().ToDictionary(w => w.Id, w => w.Value, StringComparer.Ordinal);
        var open = store.AllItems().Where(i => !i.Value.IsCompleted && !i.Value.IsSubtask).ToList();

        if (query.IsEmpty)
        {
            // Due soonest first: the overdue ones and the ones due today.
            var endOfToday = new DateTimeOffset(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(now));
            foreach (var (id, item, _) in open
                         .Where(i => workspaces.GetValueOrDefault(i.Value.WorkspaceId)?.Kind == WorkspaceKind.Tasks)
                         .Select(i => (i.Id, i.Value, due: i.Value.DueMoment(TimeZoneInfo.Local)))
                         .Where(x => x.due is { } d && d < endOfToday)
                         .OrderBy(x => x.due)
                         .Take(Suggestions))
            {
                yield return Task(id, item, workspaces[item.WorkspaceId], now, 0.09);
            }
            yield break;
        }

        var results = new List<PaletteItem>();
        foreach (var (id, item, _) in open)
        {
            if (workspaces.GetValueOrDefault(item.WorkspaceId) is not { Kind: WorkspaceKind.Tasks } ws) continue;
            var score = query.Score(item.Title, item.Notes);
            if (score > 0) results.Add(Task(id, item, ws, now, score));
        }

        // The debt book: one result per person with an open balance.
        if (workspaces.FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Debts) is { Key: { } bookId, Value: { } book })
        {
            foreach (var person in DebtLedger.Open(store.Items(bookId)).Where(p => !p.IsSettled))
            {
                var score = query.Score(person.Name);
                if (score <= 0) continue;
                results.Add(new PaletteItem(person.Name, $"Debt · {TrackerFormat.Balance(person.Balance, book.Currency)}", PaletteKind.Debt, score,
                    () => Show(bookId)));
            }
        }

        foreach (var item in results.OrderByDescending(r => r.Score).Take(MaxResults)) yield return item;
    }

    private PaletteItem Task(string id, TrackerItem item, TrackerWorkspace ws, DateTimeOffset now, double score)
    {
        var due = item.DueMoment(TimeZoneInfo.Local) is null ? "" : " · " + TrackerFormat.Due(item, now);
        return new PaletteItem(item.Title, $"Task in {ws.Name}{due}", PaletteKind.Task, score, () => Show(item.WorkspaceId));
    }

    private void Show(string workspaceId)
    {
        services.GetRequiredService<IShellNavigation>().ShowPage(typeof(TrackerContentPage));
        viewModel.ShowWorkspace(workspaceId);
    }
}
