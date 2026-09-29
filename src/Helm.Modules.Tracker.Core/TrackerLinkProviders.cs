using Helm.Core.Links;
using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tasks as link targets (notes link to them). A task that repeats is linked as its series, so the note stays with
/// every day's task rather than with the day it was linked on.
/// </summary>
/// <param name="open">Shows a workspace with the given item in the platform's Tracker page.</param>
public sealed class TaskLinkProvider : ILinkProvider
{
    private readonly TrackerStore _store;
    private readonly Action<string, string> _open;

    public TaskLinkProvider(TrackerStore store, Action<string, string> open)
    {
        _store = store;
        _open = open;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Kind => LinkKinds.Task;

    public string KindName => "Task";

    public event EventHandler? Changed;

    /// <summary>The id a link to this item uses: the series of a repeating task, else the item.</summary>
    public static string LinkId(string itemId, TrackerItem item) => item.SeriesId ?? itemId;

    public LinkTarget? Resolve(string id) => Find(id) is { } found ? Target(id, found.Id, found.Value) : null;

    public IEnumerable<LinkTarget> Search(string text, int max)
    {
        var terms = TextSearch.Terms(text);
        var tasks = Tasks().ToList();
        if (terms.Count == 0)
        {
            return tasks.Where(t => !t.Value.IsCompleted).OrderByDescending(t => t.Value.CreatedAt).Take(max)
                .Select(t => Target(LinkId(t.Id, t.Value), t.Id, t.Value)).ToList();
        }
        return tasks
            .Select(t => (t, Score: TextSearch.Score(terms, t.Value.Title, t.Value.Notes) * (t.Value.IsCompleted ? 0.5 : 1)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => Target(LinkId(x.t.Id, x.t.Value), x.t.Id, x.t.Value))
            .ToList();
    }

    public void Open(string id)
    {
        if (Find(id) is { } found) _open(found.Value.WorkspaceId, found.Id);
    }

    /// <summary>Top-level tasks of task lists, one per repeating series (its latest day).</summary>
    private IEnumerable<SyncedItem<TrackerItem>> Tasks()
    {
        var lists = _store.Workspaces().Where(w => w.Value.Kind == WorkspaceKind.Tasks).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return _store.AllItems()
            .Where(i => !i.Value.IsSubtask && lists.Contains(i.Value.WorkspaceId))
            .GroupBy(i => LinkId(i.Id, i.Value), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(i => i.Value.OccurrenceDate ?? DateOnly.MinValue).First());
    }

    private SyncedItem<TrackerItem>? Find(string id)
    {
        if (_store.GetItem(id) is { } item) return new SyncedItem<TrackerItem>(id, item, item.CreatedAt);
        return Tasks().FirstOrDefault(t => t.Value.SeriesId == id);
    }

    private LinkTarget Target(string linkId, string itemId, TrackerItem item)
    {
        var workspace = _store.GetWorkspace(item.WorkspaceId)?.Name ?? "";
        var state = item.IsCompleted ? " · done" : item.DueMoment(TimeZoneInfo.Local) is null ? "" : " · " + TrackerFormat.Due(item, _store.Now);
        return new LinkTarget(new LinkRef(Kind, linkId), item.Title, $"Task in {workspace}{state}");
    }
}

/// <summary>People in the debt book as link targets, by the key of their name (every entry of theirs, settled or not).</summary>
/// <param name="open">Shows the debt book with the given person open.</param>
public sealed class PersonLinkProvider : ILinkProvider
{
    private readonly TrackerStore _store;
    private readonly Action<string, string> _open;

    public PersonLinkProvider(TrackerStore store, Action<string, string> open)
    {
        _store = store;
        _open = open;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Kind => LinkKinds.Person;

    public string KindName => "Person";

    public event EventHandler? Changed;

    public LinkTarget? Resolve(string id) => People().FirstOrDefault(p => p.Person.Key == id) is { Person: not null } found
        ? Target(found.Person, found.Currency) : null;

    public IEnumerable<LinkTarget> Search(string text, int max)
    {
        var terms = TextSearch.Terms(text);
        var people = People().ToList();
        if (terms.Count == 0) return people.Take(max).Select(p => Target(p.Person, p.Currency)).ToList();
        return people
            .Select(p => (p, Score: TextSearch.Score(terms, p.Person.Name)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => Target(x.p.Person, x.p.Currency))
            .ToList();
    }

    public void Open(string id)
    {
        if (Book() is { } book) _open(book.Id, id);
    }

    private SyncedItem<TrackerWorkspace>? Book() => _store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Debts);

    /// <summary>Everyone in the debt book, open balances first; a settled person once, with their latest settlement.</summary>
    private IEnumerable<(DebtPerson Person, string Currency)> People()
    {
        if (Book() is not { } book) return [];
        var items = _store.Items(book.Id);
        var open = DebtLedger.Open(items);
        var known = open.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var settled = DebtLedger.Settled(items).Where(p => known.Add(p.Key));
        return open.Concat(settled).Select(p => (p, book.Value.Currency));
    }

    private LinkTarget Target(DebtPerson person, string currency) =>
        new(new LinkRef(Kind, person.Key), person.Name, "Debt · " + (person.IsSettled ? "settled" : TrackerFormat.Balance(person.Balance, currency)));
}
