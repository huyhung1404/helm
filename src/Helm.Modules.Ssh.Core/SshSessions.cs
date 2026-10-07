namespace Helm.Modules.Ssh;

/// <summary>A live connection to a server that can run menu commands on its own channel (an <see cref="SshSession"/>; tests use a fake).</summary>
public interface ISshChannel : IMenuRunner
{
    bool IsConnected { get; }
}

/// <summary>
/// The sessions open now, by server id. <see cref="SshViewModel"/> owns them and keeps this up to date, so other parts
/// of Helm (the AI agents' tools) can find a connected server without connecting themselves. Thread-safe.
/// </summary>
public sealed class SshSessions
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISshChannel> _sessions = new(StringComparer.Ordinal);

    public void Register(string hostId, ISshChannel session)
    {
        lock (_gate) _sessions[hostId] = session;
    }

    /// <summary>Forgets the server's session, only if it is still <paramref name="session"/> (null: whichever it is).</summary>
    public void Unregister(string hostId, ISshChannel? session = null)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(hostId, out var current) && (session is null || ReferenceEquals(current, session))) _sessions.Remove(hostId);
        }
    }

    public void Clear()
    {
        lock (_gate) _sessions.Clear();
    }

    /// <summary>The server's session while it is connected; null otherwise.</summary>
    public ISshChannel? Connected(string hostId)
    {
        lock (_gate) return _sessions.TryGetValue(hostId, out var session) && session.IsConnected ? session : null;
    }
}
