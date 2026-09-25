namespace Helm.App;

/// <summary>
/// Named-mutex single instance guard. A second launch signals the named event so the first instance brings
/// its window to the front, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Helm.SingleInstance";
    private const string ActivateEventName = @"Local\Helm.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;
    private bool _owned;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool owned)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = owned;
    }

    public bool IsFirstInstance => _owned;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try { createdNew = mutex.WaitOne(0); } // previous owner exited without releasing
            catch (AbandonedMutexException) { createdNew = true; }
        }
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstance(mutex, activate, createdNew);
    }

    /// <summary>Asks the running instance to show itself.</summary>
    public void SignalFirstInstance() => _activate.Set();

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch signals us.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Releases the mutex early so a replacement process (restart/reset) can take over.</summary>
    public void Release()
    {
        _wait?.Unregister(null);
        _wait = null;
        if (!_owned) return;
        _owned = false;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
        _activate.Dispose();
    }
}
