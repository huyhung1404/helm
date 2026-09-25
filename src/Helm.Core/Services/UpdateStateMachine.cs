namespace Helm.Core.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Downloaded,
    Applying,
    Failed,
    NotInstalled,
}

public enum UpdateTrigger
{
    CheckStarted,
    NoUpdate,
    UpdateFound,
    DownloadStarted,
    DownloadCompleted,
    ApplyStarted,
    Failed,
    NotInstalled,
}

/// <summary>
/// The update lifecycle: Idle → Checking → (UpToDate | UpdateAvailable) → Downloading → Downloaded → Applying.
/// Failures return to <see cref="UpdateState.Failed"/> (retryable); <see cref="UpdateState.NotInstalled"/> is terminal.
/// Invalid triggers are rejected so the UI can never show e.g. "Restart to update" before a download finished.
/// </summary>
public sealed class UpdateStateMachine
{
    private static readonly Dictionary<(UpdateState, UpdateTrigger), UpdateState> s_transitions = new()
    {
        [(UpdateState.Idle, UpdateTrigger.CheckStarted)] = UpdateState.Checking,
        [(UpdateState.UpToDate, UpdateTrigger.CheckStarted)] = UpdateState.Checking,
        [(UpdateState.UpdateAvailable, UpdateTrigger.CheckStarted)] = UpdateState.Checking,
        [(UpdateState.Failed, UpdateTrigger.CheckStarted)] = UpdateState.Checking,

        [(UpdateState.Checking, UpdateTrigger.NoUpdate)] = UpdateState.UpToDate,
        [(UpdateState.Checking, UpdateTrigger.UpdateFound)] = UpdateState.UpdateAvailable,
        [(UpdateState.Checking, UpdateTrigger.DownloadCompleted)] = UpdateState.Downloaded, // already downloaded earlier

        [(UpdateState.UpdateAvailable, UpdateTrigger.DownloadStarted)] = UpdateState.Downloading,
        [(UpdateState.Failed, UpdateTrigger.DownloadStarted)] = UpdateState.Downloading,
        [(UpdateState.Downloading, UpdateTrigger.DownloadCompleted)] = UpdateState.Downloaded,

        [(UpdateState.Downloaded, UpdateTrigger.ApplyStarted)] = UpdateState.Applying,
    };

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public event EventHandler<UpdateState>? Changed;

    public bool CanFire(UpdateTrigger trigger) => Next(trigger) is not null;

    /// <summary>Applies <paramref name="trigger"/>; returns false (state unchanged) when it is not valid now.</summary>
    public bool TryFire(UpdateTrigger trigger)
    {
        if (Next(trigger) is not { } next) return false;
        State = next;
        Changed?.Invoke(this, next);
        return true;
    }

    public void Fire(UpdateTrigger trigger)
    {
        if (!TryFire(trigger)) throw new InvalidOperationException($"Cannot apply {trigger} while {State}.");
    }

    private UpdateState? Next(UpdateTrigger trigger)
    {
        if (State == UpdateState.NotInstalled) return null;
        if (trigger == UpdateTrigger.NotInstalled) return UpdateState.NotInstalled;
        if (trigger == UpdateTrigger.Failed) return State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Applying ? UpdateState.Failed : null;
        return s_transitions.TryGetValue((State, trigger), out var next) ? next : null;
    }
}
