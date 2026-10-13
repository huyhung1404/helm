using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Text;

namespace Helm.Shell.ViewModels;

/// <summary>One row of General → Running in background.</summary>
public sealed partial class BackgroundActivityRow : ObservableObject
{
    private readonly BackgroundActivity _activity;

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    private bool _isStopping;

    public BackgroundActivityRow(BackgroundActivity activity, string since)
    {
        _activity = activity;
        _detail = activity.Detail;
        Since = since;
    }

    public BackgroundActivity Activity => _activity;

    public string Title => _activity.Title;

    /// <summary>"Since 09:42".</summary>
    public string Since { get; }

    internal void Refresh() => Detail = _activity.Detail;

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsStopping) return;
        IsStopping = true;
        await _activity.StopAsync().ConfigureAwait(true);
    }
}

/// <summary>
/// General → Running in background, on both apps: every tool that keeps something running (a listener, a connection,
/// a service) and a button to stop it at once.
/// </summary>
public sealed class BackgroundActivitiesViewModel : ObservableObject
{
    private readonly IBackgroundActivities _activities;
    private readonly IUiDispatcher _ui;

    public BackgroundActivitiesViewModel(IBackgroundActivities activities, IUiDispatcher ui)
    {
        _activities = activities;
        _ui = ui;
        activities.Changed += (_, _) => _ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<BackgroundActivityRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    private void Refresh()
    {
        var current = _activities.Current;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(Items[i].Activity)) Items.RemoveAt(i);
        }
        foreach (var activity in current)
        {
            var row = Items.FirstOrDefault(r => r.Activity == activity);
            if (row is null) Items.Add(new BackgroundActivityRow(activity, "Since " + DateTimeText.Clock(activity.Since.TimeOfDay)));
            else row.Refresh();
        }
        OnPropertyChanged(nameof(HasItems));
    }
}
