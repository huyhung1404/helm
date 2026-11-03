using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai;
using Helm.App.Ai;
using Helm.Core.Services;
using Helm.Core.Sync;

namespace Helm.App.ViewModels;

/// <summary>One agent of this PC's proxy, as the Proxy part lists it.</summary>
internal sealed partial class AgentRow(AgentProfile profile, string status, bool ready, bool signedIn, bool first, bool last) : ObservableObject
{
    public string Id { get; } = profile.Id;
    public string Name { get; } = profile.Name;
    public string Kind { get; } = AgentKinds.NameOf(profile.Kind);
    public string Status { get; } = ready ? "Ready: takes jobs" : status;
    public bool IsReady { get; } = ready;
    public bool IsOff { get; } = !profile.Enabled;

    /// <summary>Sign in shows only until it is signed in: another account is Remove, then Add again.</summary>
    public bool NeedsSignIn { get; } = !signedIn;
    public bool CanMoveUp { get; } = !first;
    public bool CanMoveDown { get; } = !last;

    [ObservableProperty] private bool _isOn = profile.Enabled;
}

/// <summary>A route policy as the Ask Helm card's choice lists it.</summary>
internal sealed record RouteOption(AiRoutePolicy Policy, string Label);

/// <summary>
/// The Proxy part of the AI page: the agents the user added to this PC (each signed in to its own account), in the
/// order Helm's AI jobs try them; Ask Helm's route; the user's VPS; and what this month's calls cost and saved.
/// </summary>
internal sealed partial class AiProxyViewModel : ObservableObject
{
    private readonly AgentRegistry _agents;
    private readonly ISyncedSettings<AiFeatureSettings> _features;
    private readonly AiUsageBook _usage;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private bool _loading;

    [ObservableProperty] private RouteOption _route;
    [ObservableProperty] private double _waitSeconds;
    [ObservableProperty] private string _monthText = "";

    public AiProxyViewModel(AgentRegistry agents, ISyncedSettings<AiFeatureSettings> features, AiUsageBook usage, IUiDispatcher ui, IDialogService dialogs,
        VpsWorkerViewModel vps)
    {
        _agents = agents;
        _features = features;
        _usage = usage;
        _ui = ui;
        _dialogs = dialogs;
        Vps = vps;
        var choice = features.Current.For(AiFeatureIds.AskHelm);
        _route = Routes.First(r => r.Policy == choice.Route);
        _waitSeconds = choice.PcWaitSeconds;
        _agents.Changed += (_, _) => _ui.Post(Refresh);
        _features.Changed += (_, _) => _ui.Post(LoadChoice);
        Refresh();
    }

    public ObservableCollection<AgentRow> Agents { get; } = [];

    /// <summary>The same agents serving the user's own VPS.</summary>
    public VpsWorkerViewModel Vps { get; }

    public bool HasAgents => Agents.Count > 0;

    public IReadOnlyList<RouteOption> Routes { get; } =
    [
        new(AiRoutePolicy.PreferPc, "Agents on a PC first, then the API"),
        new(AiRoutePolicy.PcOnly, "Agents on a PC only (never the API)"),
        new(AiRoutePolicy.ApiOnly, "The API only"),
    ];

    /// <summary>The page came into view: sign-ins may have happened in their terminals meanwhile.</summary>
    public void Refresh()
    {
        Agents.Clear();
        var profiles = _agents.Profiles;
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            var status = _agents.Status(p);
            var row = new AgentRow(p, status, status == "Ready", _agents.SignedIn(p), i == 0, i == profiles.Count - 1);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AgentRow.IsOn)) _agents.SetEnabled(row.Id, row.IsOn);
            };
            Agents.Add(row);
        }
        OnPropertyChanged(nameof(HasAgents));
        var spent = _usage.SpentThisMonth();
        var saved = _usage.SavedThisMonth();
        var calls = _usage.ThisMonth().Sum(r => r.Calls);
        MonthText = calls == 0
            ? "No AI call this month yet."
            : $"{calls} calls this month: {Usd(spent)} on the API, {Usd(saved)} saved by agents (what the API would have cost).";
    }

    partial void OnRouteChanged(RouteOption value) => Save();

    partial void OnWaitSecondsChanged(double value) => Save();

    private void LoadChoice()
    {
        _loading = true;
        var choice = _features.Current.For(AiFeatureIds.AskHelm);
        Route = Routes.First(r => r.Policy == choice.Route);
        WaitSeconds = choice.PcWaitSeconds;
        _loading = false;
    }

    private void Save()
    {
        if (_loading) return;
        var wait = (int)Math.Clamp(Math.Round(WaitSeconds), 2, 60);
        _features.Update(s =>
        {
            var choice = s.For(AiFeatureIds.AskHelm).Copy();
            if (choice.Route == Route.Policy && choice.PcWaitSeconds == wait) return;
            choice.Route = Route.Policy;
            choice.PcWaitSeconds = wait;
            s.Features[AiFeatureIds.AskHelm] = choice;
        });
    }

    [RelayCommand]
    private void AddCodex() => _agents.Add(AgentKinds.Codex);

    [RelayCommand]
    private void AddClaudeCode() => _agents.Add(AgentKinds.ClaudeCode);

    [RelayCommand]
    private void AddGrok() => _agents.Add(AgentKinds.Grok);

    [RelayCommand]
    private void SignIn(AgentRow? row)
    {
        if (row is not null) _agents.SignIn(row.Id);
    }

    [RelayCommand]
    private void CheckAgain() => Refresh();

    [RelayCommand]
    private void MoveUp(AgentRow? row)
    {
        if (row is not null) _agents.Move(row.Id, -1);
    }

    [RelayCommand]
    private void MoveDown(AgentRow? row)
    {
        if (row is not null) _agents.Move(row.Id, 1);
    }

    [RelayCommand]
    private async Task Remove(AgentRow? row)
    {
        if (row is null) return;
        var ok = await _dialogs.ConfirmAsync($"Remove {row.Name}?",
            "Helm stops sending AI jobs to it and forgets its sign-in on this PC (the account itself is not touched).", "Remove");
        if (ok) _agents.Remove(row.Id);
    }

    private static string Usd(decimal usd) => usd.ToString(usd < 0.01m && usd > 0 ? "$0.0000" : "$0.00", CultureInfo.InvariantCulture);
}
