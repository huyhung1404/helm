using Helm.Core.Links;
using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions as link targets: a note can be linked to a mission (and the mission page shows its notes). Opening one shows
/// it on the Missions page.
/// </summary>
public sealed class MissionLinkProvider : ILinkProvider
{
    private readonly MissionsStore _store;
    private readonly Action<string> _open;

    public MissionLinkProvider(MissionsStore store, Action<string> open)
    {
        _store = store;
        _open = open;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Kind => LinkKinds.Mission;
    public string KindName => "Mission";
    public event EventHandler? Changed;

    public LinkTarget? Resolve(string id) => _store.GetMission(id) is { } m ? Target(id, m) : null;

    /// <summary>Missions whose title or goal match; with no text, the ones in progress or planned first, then the rest.</summary>
    public IEnumerable<LinkTarget> Search(string text, int max)
    {
        var terms = TextSearch.Terms(text);
        var missions = _store.Missions();
        if (terms.Count == 0)
            return missions.OrderBy(m => m.Value.Status is MissionStatus.Completed or MissionStatus.Abandoned)
                .ThenByDescending(m => m.Value.CreatedAt).Take(max).Select(m => Target(m.Id, m.Value)).ToList();
        return missions
            .Select(m => (m, Score: TextSearch.Score(terms, m.Value.Title, m.Value.Goal)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(max)
            .Select(x => Target(x.m.Id, x.m.Value)).ToList();
    }

    public void Open(string id)
    {
        if (_store.GetMission(id) is not null) _open(id);
    }

    private LinkTarget Target(string id, Mission m)
    {
        var steps = _store.Steps(id);
        var state = m.Status switch
        {
            MissionStatus.Active => $"step {Math.Min(steps.Count(s => s.Value.IsDone) + 1, steps.Count)} of {steps.Count}",
            _ => MissionsFormat.Status(m.Status).ToLowerInvariant(),
        };
        return new LinkTarget(new LinkRef(Kind, id), m.Title, $"Mission · {state}");
    }
}
