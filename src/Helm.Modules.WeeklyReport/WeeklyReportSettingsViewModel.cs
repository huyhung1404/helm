using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.SlackBot;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.WeeklyReport;

/// <summary>Weekly Report's settings page: where the repositories are, who the user is in git, notes for every report.</summary>
public sealed partial class WeeklyReportSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore<WeeklyReportSettings> _store;
    private readonly IServiceProvider _services;
    private bool _loading;

    [ObservableProperty] private string _repoFolders = "";
    [ObservableProperty] private string _authors = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _model = "";

    public WeeklyReportSettingsViewModel(WeeklyReportModule module, ISettingsStoreFactory settings, IServiceProvider services)
    {
        Module = module;
        _store = settings.Get<WeeklyReportSettings>(WeeklyReportModule.ModuleId);
        _services = services;
        _loading = true;
        var s = _store.Current;
        RepoFolders = string.Join(Environment.NewLine, s.RepoFolders);
        Authors = string.Join(Environment.NewLine, s.Authors);
        Notes = s.Notes;
        Model = s.Model;
        _loading = false;
    }

    public WeeklyReportModule Module { get; }

    /// <summary>Shown when no folder is set: where Helm looks then.</summary>
    public string UsualFolders => WeeklyReportSettings.UsualFolders() is { Count: > 0 } f
        ? "Empty: " + string.Join(", ", f) + "."
        : "Empty: no usual folder exists on this PC; add yours.";

    public IReadOnlyList<SlackBotChoice<string>> Models { get; } =
    [
        new("", "Claude Code's default"),
        new("sonnet", "Sonnet"),
        new("opus", "Opus"),
    ];

    partial void OnRepoFoldersChanged(string value) => Save(s => s.RepoFolders = Lines(value));

    partial void OnAuthorsChanged(string value) => Save(s => s.Authors = Lines(value));

    partial void OnNotesChanged(string value) => Save(s => s.Notes = value);

    partial void OnModelChanged(string value) => Save(s => s.Model = value);

    [RelayCommand]
    private void OpenContent() => _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(WeeklyReportContentPage));

    [RelayCommand]
    private void ShowAllAgain() => _store.Update(s => s.Hidden.Clear());

    internal static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private void Save(Action<WeeklyReportSettings> change)
    {
        if (!_loading) _store.Update(change);
    }
}
