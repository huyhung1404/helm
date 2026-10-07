using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.App.ViewModels;

/// <summary>What AI agents may do without asking, and what they have asked (the Permissions card of the AI &amp; MCP page).</summary>
internal sealed partial class McpPermissionsViewModel : ObservableObject
{
    /// <summary>The activity list shows this many of the newest entries (the file keeps <see cref="McpActivityLog.MaxEntries"/>).</summary>
    private const int ShownEntries = 50;

    private readonly McpConsentPolicy _policy;
    private readonly ISettingsStore<McpSettings> _settings;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty]
    private bool _askBeforeChanges;

    [ObservableProperty]
    private string _activitySummary = "";

    public McpPermissionsViewModel(McpConsentPolicy policy, ISettingsStoreFactory settings, IUiDispatcher ui)
    {
        _policy = policy;
        _settings = settings.Get<McpSettings>(McpSettings.StoreId);
        _ui = ui;
        _loading = true;
        _askBeforeChanges = _settings.Current.AskBeforeChanges;
        _loading = false;
        _settings.Changed += (_, s) => _ui.Post(() =>
        {
            _loading = true;
            AskBeforeChanges = s.AskBeforeChanges;
            _loading = false;
        });
        _policy.AllowancesChanged += (_, _) => _ui.Post(LoadAllowances);
        _policy.Log.Changed += (_, _) => _ui.Post(LoadActivity);
        LoadAllowances();
        LoadActivity();
    }

    public bool HelmElevated => _policy.HelmElevated;

    public string ElevationText => HelmElevated
        ? "Helm runs as Administrator: every change an AI agent makes through Helm is asked first, and so is every command on a server."
        : "Helm does not run as Administrator: changes to your Helm data run without asking (unless the switch above is on). Commands on a server are always asked.";

    /// <summary>"Allow for this session" answers still in force.</summary>
    public ObservableCollection<McpAllowanceRow> Allowances { get; } = [];

    public ObservableCollection<McpActivityRow> Activity { get; } = [];

    public bool HasAllowances => Allowances.Count > 0;

    public bool HasActivity => Activity.Count > 0;

    partial void OnAskBeforeChangesChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.AskBeforeChanges = value);
    }

    [RelayCommand]
    private void Revoke(McpAllowanceRow row) => _policy.Revoke(row.Allowance);

    [RelayCommand]
    private void ClearActivity() => _policy.Log.Clear();

    private void LoadAllowances()
    {
        Allowances.Clear();
        foreach (var allowance in _policy.Allowances) Allowances.Add(new McpAllowanceRow(allowance));
        OnPropertyChanged(nameof(HasAllowances));
    }

    private void LoadActivity()
    {
        var entries = _policy.Log.Entries;
        Activity.Clear();
        foreach (var entry in entries.Take(ShownEntries)) Activity.Add(new McpActivityRow(entry));
        ActivitySummary = entries.Count > ShownEntries
            ? $"The newest {ShownEntries} of {entries.Count}. Kept on this device only, never synced."
            : "Kept on this device only, never synced.";
        OnPropertyChanged(nameof(HasActivity));
    }
}

internal sealed class McpAllowanceRow(McpSessionAllowance allowance)
{
    public McpSessionAllowance Allowance { get; } = allowance;

    public string Title => Allowance.Title;

    public string Detail => Allowance.Target is { } target
        ? $"{Allowance.ClientName} · {Allowance.Tool} on {target} · since {Allowance.Since:HH:mm}"
        : $"{Allowance.ClientName} · {Allowance.Tool} · since {Allowance.Since:HH:mm}";
}

internal sealed class McpActivityRow(McpActivityEntry entry)
{
    public string Title => entry.Title;

    public string Detail
    {
        get
        {
            var result = entry.Result switch { "ok" => " · done", "error" => " · failed", _ => "" };
            var target = entry.Target is { } t ? $" on {t}" : "";
            return $"{entry.Time.LocalDateTime:g} · {entry.Client} · {entry.Tool}{target} · {entry.Answer}{result}";
        }
    }

    public string? Details => entry.Details;

    /// <summary>Denied, refused or failed: drawn in the caution color.</summary>
    public bool NotDone => entry.Result != "ok";
}
