using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai.Assistant;

namespace Helm.Ai;

/// <summary>
/// One feature's settings as the Features table (PC) and the AI features list and detail (phone) show them. Every
/// change is saved at once to the synced choice of that feature (<see cref="AiFeaturesViewModel"/>).
/// </summary>
public sealed partial class AiFeatureRow : ObservableObject
{
    /// <summary>The highest daily cap the settings take (USD of API calls a day).</summary>
    internal const double MaxCapUsd = 20;

    private readonly Action<string, Action<AiFeatureChoice>> _save;
    private bool _showing;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private AiRouteOption _route = AiRouteOption.All[0];
    [ObservableProperty] private AssistantModelOption? _model;
    [ObservableProperty] private AiEffortOption _effort = AiEffortOption.All[0];

    /// <summary><see cref="AiFeatureChoice.DailyCapUsd"/>: what the feature may spend on the API a day.</summary>
    [ObservableProperty] private double _cap;

    /// <summary>The chat models of the tested keys (the one picked too, when no key offers it now).</summary>
    [ObservableProperty] private IReadOnlyList<AssistantModelOption> _models = [];

    internal AiFeatureRow(AiFeatureInfo info, Action<string, Action<AiFeatureChoice>> save)
    {
        Id = info.Id;
        Name = info.Name;
        IsAutomatic = info.Automatic;
        Tool = info.Tool;
        Symbol = info.Symbol;
        _save = save;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsAutomatic { get; }

    /// <summary>The tool whose icon the row shows (a module id), or null: <see cref="Symbol"/>.</summary>
    public string? Tool { get; }

    /// <summary>The Fluent symbol without a size, when the tool's icon is not there.</summary>
    public string Symbol { get; }

    public IReadOnlyList<AiRouteOption> Routes => AiRouteOption.All;

    public IReadOnlyList<AiEffortOption> Efforts => AiEffortOption.All;

    /// <summary>Effort is Claude's: a Grok model has none.</summary>
    public bool CanChooseEffort => Model is null || Model.Provider == AiProviders.Claude;

    /// <summary>The cap only matters on a route that can reach the API.</summary>
    public bool CanReachApi => Policy != AiRoutePolicy.PcOnly;

    public string CapText => $"{Usd(Cap)} a day on the API";

    /// <summary>What the row costs at most: 0đ on the PC's agents, the cap where the API may be used (worst case).</summary>
    public string CostText => !IsOn ? "Off" : Policy switch
    {
        AiRoutePolicy.PcOnly => IsAutomatic ? "Runs on your PC agents · 0đ" : "PC agents · 0đ",
        AiRoutePolicy.ApiOnly => $"API · ≤ {Usd(Cap)}/day",
        _ => "PC agents first · 0đ",
    };

    /// <summary>The second badge of <see cref="AiRoutePolicy.PreferPc"/>: what the API fallback may cost.</summary>
    public string CostDetail => IsOn && Policy == AiRoutePolicy.PreferPc ? $"API fallback ≤ {Usd(Cap)}/day" : "";

    public bool HasCostDetail => CostDetail.Length > 0;

    /// <summary>Every call is paid: the badge stands out.</summary>
    public bool IsCostWarning => IsOn && Policy == AiRoutePolicy.ApiOnly;

    /// <summary>Runs for free on the PC's agents.</summary>
    public bool IsFree => IsOn && Policy != AiRoutePolicy.ApiOnly;

    /// <summary>The phone list's one line: where it runs and with which model, or Off.</summary>
    public string StateText => !IsOn ? "Off"
        : $"{(Policy == AiRoutePolicy.PcOnly ? "PC agents · 0đ" : Route?.Label)} · {Model?.Name ?? "no model"}";

    // A list control may clear its selection for a moment while it is rebuilt.
    private AiRoutePolicy Policy => Route?.Policy ?? AiRoutePolicy.PreferPc;

    /// <summary>Shows the synced choice without saving it back.</summary>
    internal void Show(AiFeatureChoice choice, IReadOnlyList<AssistantModelOption> models, string description)
    {
        _showing = true;
        try
        {
            Description = description;
            if (!Models.SequenceEqual(models)) Models = models;
            IsOn = choice.Enabled;
            Route = AiRouteOption.For(choice.Route);
            Model = models.FirstOrDefault(m => m.Provider.Id == choice.Provider && m.Model == choice.Model);
            Effort = AiEffortOption.For(choice.Effort);
            Cap = Clamp((double)choice.DailyCapUsd);
        }
        finally
        {
            _showing = false;
        }
    }

    internal static double Clamp(double usd) => double.IsNaN(usd) ? 0 : Math.Round(Math.Clamp(usd, 0, MaxCapUsd), 2);

    internal static string Usd(double usd) => usd.ToString(usd is > 0 and < 0.01 ? "$0.0000" : "$0.00", CultureInfo.InvariantCulture);

    partial void OnIsOnChanged(bool value)
    {
        Badges();
        Save(c => c.Enabled = value);
    }

    partial void OnRouteChanged(AiRouteOption value)
    {
        // A list being rebuilt (WPF clears the selection first) is not the user's choice.
        if (value is null) return;
        OnPropertyChanged(nameof(CanReachApi));
        Badges();
        Save(c => c.Route = value.Policy);
    }

    partial void OnModelChanged(AssistantModelOption? value)
    {
        OnPropertyChanged(nameof(CanChooseEffort));
        OnPropertyChanged(nameof(StateText));
        if (value is not null) Save(c => (c.Provider, c.Model) = (value.Provider.Id, value.Model));
    }

    partial void OnEffortChanged(AiEffortOption value)
    {
        if (value is not null) Save(c => c.Effort = value.Id);
    }

    partial void OnCapChanged(double value)
    {
        var clamped = Clamp(value);
        if (clamped != value)
        {
            Cap = clamped;
            return;
        }
        OnPropertyChanged(nameof(CapText));
        Badges();
        Save(c => c.DailyCapUsd = (decimal)clamped);
    }

    /// <summary>The phone's stepper: ten cents at a time up to a dollar, fifty above.</summary>
    [RelayCommand]
    private void MoreCap() => Cap = Clamp(Cap + (Cap < 1 ? 0.1 : 0.5));

    [RelayCommand]
    private void LessCap() => Cap = Clamp(Cap - (Cap <= 1 ? 0.1 : 0.5));

    private void Badges()
    {
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(CostDetail));
        OnPropertyChanged(nameof(HasCostDetail));
        OnPropertyChanged(nameof(IsCostWarning));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(StateText));
    }

    private void Save(Action<AiFeatureChoice> change)
    {
        if (!_showing) _save(Id, change);
    }
}
