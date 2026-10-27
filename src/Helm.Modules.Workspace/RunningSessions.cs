namespace Helm.Modules.Workspace;

/// <summary>
/// The Claude sessions the connected VS Code windows run right now (<see cref="ClaudeProcesses"/>), looked at again
/// every <see cref="Every"/>: <see cref="Changed"/> says when a window got or lost a session, or one went busy or idle.
/// </summary>
internal sealed class RunningSessions : IDisposable
{
    /// <summary>How often the windows' claude processes are looked at.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    private readonly ClaudeProcesses _processes;
    private readonly Func<IReadOnlyCollection<int>> _hosts;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<ClaudeProcess> _current = [];

    public RunningSessions(ClaudeProcesses processes, Func<IReadOnlyCollection<int>> hosts, TimeProvider time)
    {
        _processes = processes;
        _hosts = hosts;
        _timer = time.CreateTimer(_ => Refresh(), null, Every, Every);
    }

    /// <summary>Raised off the UI thread after <see cref="Current"/> changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ClaudeProcess> Current => Volatile.Read(ref _current);

    /// <summary>The window (extension host pid) running <paramref name="sessionId"/>, or null.</summary>
    public int? HostOf(string sessionId) => Current.FirstOrDefault(p => p.SessionId == sessionId)?.HostPid;

    /// <summary>The sessions the window of <paramref name="hostPid"/> runs.</summary>
    public HashSet<string> Of(int hostPid) => Current.Where(p => p.HostPid == hostPid).Select(p => p.SessionId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Looks now (a window connected or left), besides the timer.</summary>
    public void Refresh()
    {
        bool changed;
        lock (_gate)
        {
            IReadOnlyList<ClaudeProcess> next;
            try
            {
                next = _processes.Scan(_hosts());
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return;
            }
            changed = !next.SequenceEqual(_current);
            Volatile.Write(ref _current, next);
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Dispose();
}
