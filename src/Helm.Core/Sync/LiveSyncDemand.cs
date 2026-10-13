namespace Helm.Core.Sync;

/// <summary>
/// Tools that need other devices' changes within seconds even while the app is in the background (a phone watching
/// its PCs). While one holds it, the phone keeps the live channel open and polls as in the foreground; otherwise the
/// background saves battery (socket closed, slow poll).
/// </summary>
public sealed class LiveSyncDemand
{
    private int _holders;

    /// <summary>Raised when the first holder arrives or the last one leaves.</summary>
    public event EventHandler? Changed;

    public bool IsHeld => Volatile.Read(ref _holders) > 0;

    /// <summary>Keeps sync live until the returned handle is disposed.</summary>
    public IDisposable Hold()
    {
        if (Interlocked.Increment(ref _holders) == 1) Changed?.Invoke(this, EventArgs.Empty);
        return new Release(this);
    }

    private void Leave()
    {
        if (Interlocked.Decrement(ref _holders) == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Release(LiveSyncDemand owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave();
        }
    }
}
