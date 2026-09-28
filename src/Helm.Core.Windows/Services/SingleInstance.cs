namespace Helm.Core.Services;

/// <summary>
/// Named-mutex single instance guard. A second launch signals the named event so the first instance brings
/// its window to the front, then exits. Only the instance that owns the mutex answers that event: the event is
/// auto-reset, so it wakes exactly one waiter, and with several waiters Windows picks any of them.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name Helm uses; tests pass their own so they never touch a running Helm.</summary>
    public const string DefaultName = "Helm";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _wait;
    private bool _owned;

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate, bool owned)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = owned;
    }

    public bool IsFirstInstance => _owned;

    public static SingleInstance Acquire(string name = DefaultName)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            try { createdNew = mutex.WaitOne(0); } // previous owner exited without releasing
            catch (AbandonedMutexException) { createdNew = true; }
        }
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        return new SingleInstance(mutex, activate, createdNew);
    }

    /// <summary>
    /// For "--allow-multiple" (dev/testing): stays out of the group entirely, so it neither blocks the installed
    /// Helm from starting as the first instance nor takes the Start menu's activation away from it.
    /// </summary>
    public static SingleInstance Detached() => new(null, null, owned: false);

    /// <summary>Asks the running instance to show itself.</summary>
    public void SignalFirstInstance() => _activate?.Set();

    /// <summary>
    /// Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch signals us.
    /// Does nothing unless this instance owns the mutex.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        if (!_owned || _activate is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Releases the mutex early so a replacement process (restart/reset) can take over.</summary>
    public void Release()
    {
        _wait?.Unregister(null);
        _wait = null;
        if (!_owned) return;
        _owned = false;
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _activate?.Dispose();
    }
}
