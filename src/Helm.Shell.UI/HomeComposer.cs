using Helm.Core.Services;
using Helm.Core.Today;
using Helm.Shell.Home;

namespace Helm.Home;

/// <summary>
/// Fills a <see cref="HomeViewModel"/> from the tools: each turned-on tool's <see cref="ITodaySource"/> becomes a card,
/// every <see cref="IHomeAlertSource"/> adds its alerts, and both are rebuilt when a source says it changed. A tick done
/// on Home does not rebuild the list under the user's finger: the ticked card stays until the next change from elsewhere.
/// </summary>
public sealed class HomeComposer : IDisposable
{
    private readonly HomeViewModel _home;
    private readonly IReadOnlyList<ITodaySource> _today;
    private readonly IReadOnlyList<IHomeAlertSource> _alerts;
    private readonly IReadOnlyDictionary<string, HomeTool> _tools;
    private readonly IHomeLinks _links;
    private readonly IUiDispatcher _ui;
    private bool _ticking;
    private bool _pending;
    private bool _disposed;

    public HomeComposer(HomeViewModel home, IEnumerable<HomeTool> tools, IEnumerable<ITodaySource> today, IEnumerable<IHomeAlertSource> alerts, IHomeLinks links, IUiDispatcher ui)
    {
        _home = home;
        _tools = tools.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _today = [.. today];
        _alerts = [.. alerts];
        _links = links;
        _ui = ui;
        foreach (var source in _today) source.Changed += OnChanged;
        foreach (var source in _alerts) source.Changed += OnChanged;
        foreach (var tool in _tools.Values) tool.PropertyChanged += OnToolChanged;
        Refresh();
    }

    /// <summary>Rebuilds the alerts and the cards now.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        _home.SetAlerts(_alerts
            .SelectMany(source => source.Current().Select(a => (Alert: a, Source: source)))
            .OrderBy(x => x.Alert.Order)
            .Select(x => new HomeAlert(x.Alert.Title, x.Alert.Detail, x.Alert.ActionText, () => x.Source.Act(x.Alert.Id))));

        var cards = new List<TodayCard>();
        foreach (var source in _today)
        {
            if (!_tools.TryGetValue(source.ModuleId, out var tool) || !tool.IsEnabled) continue;
            if (source.Build() is not { } list) continue;
            var items = list.Entries.Select(e => new TodayItem(e.Text, e.Detail, e.IsLate, e.CanTick ? item => Tick(source, e.Id, item) : null) { IsDone = e.IsDone });
            var moduleId = list.ModuleId;
            cards.Add(new TodayCard(tool, list.Headline, items, list.ActionText, () => { _links.OpenTool(moduleId); return Task.CompletedTask; }, list.Urgency));
        }
        _home.SetToday(cards);
    }

    private void Tick(ITodaySource source, string entryId, TodayItem item)
    {
        _ticking = true;
        try { source.Tick(entryId, item.IsDone); }
        finally { _ticking = false; }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_ticking || _disposed) return;
        // Many changes at once (a sync pull) become one rebuild.
        if (_pending) return;
        _pending = true;
        _ui.Post(() =>
        {
            _pending = false;
            Refresh();
        });
    }

    private void OnToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeTool.IsEnabled)) OnChanged(sender, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var source in _today) source.Changed -= OnChanged;
        foreach (var source in _alerts) source.Changed -= OnChanged;
        foreach (var tool in _tools.Values) tool.PropertyChanged -= OnToolChanged;
    }
}
