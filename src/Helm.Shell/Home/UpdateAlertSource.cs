using Helm.Core.Services;
using Helm.Core.Today;

namespace Helm.Shell.Home;

/// <summary>Home's alert for an update: ready to install, or found and waiting to be downloaded.</summary>
public sealed class UpdateAlertSource : IHomeAlertSource
{
    private readonly IUpdateService _updates;
    private readonly IHomeLinks _links;

    public UpdateAlertSource(IUpdateService updates, IHomeLinks links)
    {
        _updates = updates;
        _links = links;
        updates.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HomeAlertData> Current()
    {
        var version = _updates.LastResult?.Version;
        return _updates.State switch
        {
            UpdateState.Downloaded => [new("update", $"Helm {version} is ready", "Restart Helm to install it.", _updates.ApplyActionText, 10)],
            UpdateState.UpdateAvailable => [new("update", $"Helm {version} is available", "Download it in General.", "Update", 10)],
            _ => [],
        };
    }

    public Task Act(string alertId)
    {
        if (_updates.State == UpdateState.Downloaded) return _updates.ApplyAndRestartAsync();
        _links.OpenGeneral("updates");
        return Task.CompletedTask;
    }
}
