using Helm.Core.Capture;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Core.Today;
using Helm.Shell.Home;
using Helm.Shell.Services;

namespace Helm.Home;

/// <summary>Home as Helm shows it: the model, filled from the tools and kept up to date, and the view over it.</summary>
public sealed class HomeScreen : IDisposable
{
    private readonly HomeComposer _composer;
    private readonly SyncEngine? _sync;
    private readonly IUiDispatcher _ui;

    public HomeScreen(ShellTools tools, IToolIcons icons, IEnumerable<ITodaySource> today, IEnumerable<IHomeAlertSource> alerts,
        IEnumerable<ICaptureTarget> targets, IHomeLinks links, IUiDispatcher ui, SyncEngine? sync = null)
    {
        _ui = ui;
        _sync = sync;
        Model = new HomeViewModel(new HomeCaptureHost(targets, tools, links, "note"), $"v{AppInfo.Version}")
        {
            CaptureIcon = icons.For("quick-capture"),
        };
        _composer = new HomeComposer(Model, tools.All, today, alerts, links, ui);
        if (sync is not null) sync.StatusChanged += OnSyncStatus;
        UpdateSyncLine();
    }

    public HomeViewModel Model { get; }

    public HomeView CreateView() => new(Model);

    private void OnSyncStatus(object? sender, SyncStatus status) => _ui.Post(UpdateSyncLine);

    private void UpdateSyncLine()
    {
        Model.SyncLine = _sync?.Status switch
        {
            null or { State: SyncState.NotConfigured } => "",
            { LastSyncedAt: { } at } => "synced " + Ago(DateTimeOffset.UtcNow - at),
            _ => "",
        };
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} minutes ago",
        < 1440 => $"{(int)span.TotalHours} hours ago",
        _ => $"{(int)span.TotalDays} days ago",
    };

    public void Dispose()
    {
        if (_sync is not null) _sync.StatusChanged -= OnSyncStatus;
        _composer.Dispose();
    }
}
