using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Diagnostics;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>One black box as the black box sheet shows it: a line about it, its events under a filter, and Helm's state then.</summary>
public sealed partial class BlackBoxSheetModel : ObservableObject
{
    private readonly IReadOnlyList<BlackBoxEventItem> _all;

    public BlackBoxSheetModel(string title, string summary, IReadOnlyList<BlackBoxEventItem> events, IEnumerable<KeyValuePair<string, string>> environment)
    {
        Title = title;
        Summary = summary;
        _all = events;
        Environment = [.. environment.Select(kv => new BugEnvLine(kv.Key, kv.Value))];
        Events = _all;
    }

    /// <summary>The sheet for a black box as it was read.</summary>
    public static BlackBoxSheetModel Of(BlackBoxSnapshot box, string title) =>
        new(title, BugText.Summary(box), BlackBoxEventItem.Of(box), box.Context);

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<BugEnvLine> Environment { get; }

    public bool HasEnvironment => Environment.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<BlackBoxEventItem> _events = [];

    public bool IsEmpty => Events.Count == 0;

    [ObservableProperty]
    private BlackBoxFilter _filter;

    partial void OnFilterChanged(BlackBoxFilter value) => Events = [.. _all.Where(e => BugText.Matches(value, e.Kind))];
}

/// <summary>One line of a black box's environment: "Helm", "0.92.0".</summary>
public sealed record BugEnvLine(string Key, string Value);
