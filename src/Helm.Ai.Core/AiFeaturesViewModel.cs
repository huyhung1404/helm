using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai.Assistant;
using Helm.Ai.Auto;
using Helm.Core.Services;
using Helm.Core.Sync;

namespace Helm.Ai;

/// <summary>The features of one kind: "On request" (the user starts them) or "Automatic" (they run by themselves).</summary>
public sealed partial class AiFeatureGroup(string title, IReadOnlyList<AiFeatureRow> rows) : ObservableObject
{
    public string Title { get; } = title;

    public IReadOnlyList<AiFeatureRow> Rows { get; } = rows;

    /// <summary>"all on your PC agents · 0đ" while every feature of the group that is on runs on the agents only.</summary>
    [ObservableProperty] private string _note = "";
}

/// <summary>
/// AI settings per feature, shared by both apps: AI → Features on the PC (one table) and General › AI features on the
/// phone (a list, a detail per feature). Each row is one <see cref="AiFeatureIds"/> feature's synced
/// <see cref="AiFeatureChoice"/>, saved at once, so a change on one device shows on the other; Ask Helm's route on the
/// Proxy tab and the automatic jobs' times (Ask Helm sets them) are the same choices.
/// </summary>
public sealed partial class AiFeaturesViewModel : ObservableObject
{
    private readonly ISyncedSettings<AiFeatureSettings> _features;
    private readonly AiUsageBook _usage;
    private readonly Func<IReadOnlyList<AssistantModelOption>> _models;
    private readonly ISyncedCollection<AiWorkerRecord>? _workers;
    private readonly TimeProvider _time;
    private IReadOnlyList<AssistantModelOption> _offered = [];
    private string _runs = "";

    [ObservableProperty] private string _summaryText = "";

    /// <summary>The feature the phone shows the detail of; null on the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsDetail))]
    private AiFeatureRow? _selected;

    /// <param name="models">The models the user can pick (<see cref="HelmAssistant.Models"/>: the tested keys' chat models).</param>
    /// <param name="workers">The PCs' agent heartbeats, for "Agents online: HUNG-PC" in the summary.</param>
    public AiFeaturesViewModel(ISyncedSettings<AiFeatureSettings> features, AiUsageBook usage, Func<IReadOnlyList<AssistantModelOption>> models,
        IUiDispatcher ui, ISyncedCollection<AiWorkerRecord>? workers = null, TimeProvider? time = null)
    {
        _features = features;
        _usage = usage;
        _models = models;
        _workers = workers;
        _time = time ?? TimeProvider.System;
        Rows = [.. AiFeatureCatalog.All.Select(f => new AiFeatureRow(f, Save))];
        Groups =
        [
            new AiFeatureGroup("On request", [.. Rows.Where(r => !r.IsAutomatic)]),
            new AiFeatureGroup("Automatic", [.. Rows.Where(r => r.IsAutomatic)]),
        ];
        features.Changed += (_, _) => ui.Post(Load);
        // A PC's heartbeat (every minute) or its going quiet changes who is online.
        if (workers is not null) workers.Changed += (_, _) => ui.Post(Summarize);
        Refresh();
    }

    public IReadOnlyList<AiFeatureRow> Rows { get; }

    public IReadOnlyList<AiFeatureGroup> Groups { get; }

    public bool IsList => Selected is null;

    public bool IsDetail => Selected is not null;

    /// <summary>The page came into view: the models (a key tested meanwhile) and today's runs may have changed.</summary>
    public void Refresh()
    {
        _offered = Offered();
        var (agent, api, usd) = _usage.Today();
        var runs = agent + api;
        _runs = runs == 0
            ? "No AI run today yet"
            : $"Today {runs} {(runs == 1 ? "run" : "runs")} · {agent} on PC agents · 0đ · {api} on API · {AiFeatureRow.Usd((double)usd)}";
        Summarize();
        Load();
    }

    /// <summary>Today's runs, then the agents online ("Agents online: HUNG-PC").</summary>
    private void Summarize() => SummaryText = _workers is null
        ? _runs
        : $"{_runs} · {AiAgentsOnline.Text(_workers.All().Select(w => w.Value), _time.GetUtcNow())}";

    /// <summary>Phone back: from a feature's detail to the list; false on the list.</summary>
    public bool Back()
    {
        if (Selected is null) return false;
        Selected = null;
        return true;
    }

    [RelayCommand]
    private void Open(AiFeatureRow? row) => Selected = row;

    [RelayCommand]
    private void Close() => Selected = null;

    private void Load()
    {
        var all = _features.Current;
        foreach (var row in Rows)
        {
            var choice = AiFeatureCatalog.Effective(all, row.Id);
            row.Show(choice, ModelsFor(choice), Describe(row.Id, choice));
        }
        foreach (var group in Groups)
        {
            var on = group.Rows.Where(r => r.IsOn).ToList();
            group.Note = on.Count > 0 && on.All(r => r.Route.Policy == AiRoutePolicy.PcOnly) ? "all on your PC agents · 0đ" : "";
        }
    }

    /// <summary>Every change goes on top of what the feature runs with now (an automatic job keeps its time).</summary>
    private void Save(string feature, Action<AiFeatureChoice> change) => _features.Update(s =>
    {
        var choice = AiFeatureCatalog.Effective(s, feature).Copy();
        change(choice);
        s.Features[feature] = choice;
    });

    /// <summary>The keys' models; with no tested key, the Claude models a PC's Claude Code runs.</summary>
    private IReadOnlyList<AssistantModelOption> Offered()
    {
        var models = _models();
        return models.Count > 0 ? models : [.. AiModels.Fallback(AiProviders.Claude).Select(m => new AssistantModelOption(AiProviders.Claude, m.Id, m.Name))];
    }

    private IReadOnlyList<AssistantModelOption> ModelsFor(AiFeatureChoice choice)
    {
        if (_offered.Any(m => m.Provider.Id == choice.Provider && m.Model == choice.Model)) return _offered;
        var provider = AiProviders.Find(choice.Provider) ?? AiProviders.Claude;
        return [.. _offered, new AssistantModelOption(provider, choice.Model, choice.Model)];
    }

    /// <summary>An automatic job's description starts with its time ("07:00 · …").</summary>
    private static string Describe(string feature, AiFeatureChoice choice)
    {
        var description = AiFeatureCatalog.Find(feature)?.Description ?? "";
        if (AutoJobCatalog.Find(feature) is not { } job) return description;
        var when = feature == AiFeatureIds.NoteTitles ? "Every hour" : AutoJobCatalog.TimeOf(choice, job).ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"{when} · {description}";
    }
}
