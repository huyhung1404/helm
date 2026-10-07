using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Helm.Modules.Ssh;

public enum SshSessionState
{
    Connecting,
    Connected,

    /// <summary>The shell ended or the session was closed from Helm.</summary>
    Closed,

    /// <summary>Connecting failed or the connection broke; <see cref="SshSession.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// One SSH connection with an interactive shell (a pseudo-terminal of type xterm-256color). It lives in the view
/// model, not in a page, so it keeps running while Helm shows something else. Output is kept in a
/// <see cref="TerminalBuffer"/> and handed to whichever terminal is attached; keys and resizes are sent from a queue,
/// so a slow network never blocks the UI thread. Events are raised on background threads.
/// </summary>
public sealed class SshSession : IDisposable, ISshChannel
{
    /// <summary>A keep-alive is sent this often, so idle connections are not dropped by NATs and firewalls.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TerminalBuffer _buffer = new();
    private Action<byte[]>? _sink;
    private SshClient? _client;
    private ShellStream? _shell;
    private Channel<object>? _outbox;
    private uint _columns;
    private uint _rows;
    private bool _closing;
    private bool _disposed;

    public SshSession(SshHost host, int columns, int rows)
    {
        Host = host;
        _columns = (uint)Math.Clamp(columns, 2, 1000);
        _rows = (uint)Math.Clamp(rows, 2, 1000);
    }

    public SshHost Host { get; }

    public SshSessionState State { get; private set; } = SshSessionState.Connecting;

    /// <summary>Why the session failed, in words; null otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>The server's software, e.g. "SSH-2.0-OpenSSH_9.6p1".</summary>
    public string? ServerVersion { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>The key the server showed when connecting stopped because it was not trusted.</summary>
    public SshHostKey? RejectedHostKey { get; private set; }

    public HostKeyMatch? RejectedMatch { get; private set; }

    public bool IsConnected => State == SshSessionState.Connected;

    /// <summary>True once the server printed something (a session that never connected has nothing to show).</summary>
    public bool HasOutput
    {
        get
        {
            lock (_gate) return _buffer.Count > 0;
        }
    }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Makes <paramref name="sink"/> the terminal that receives output from now on and returns what was printed so far,
    /// atomically, so nothing is lost or shown twice. The sink is called on a background thread inside a lock: it must
    /// only queue the bytes.
    /// </summary>
    public byte[] Attach(Action<byte[]> sink)
    {
        lock (_gate)
        {
            _sink = sink;
            return _buffer.Snapshot();
        }
    }

    public void Detach(Action<byte[]> sink)
    {
        lock (_gate)
        {
            if (_sink == sink) _sink = null;
        }
    }

    /// <summary>
    /// Connects and opens the shell. Can be called again after a failure (for example once the server's key is trusted).
    /// Returns false on failure, with <see cref="Error"/> and possibly <see cref="RejectedHostKey"/> set.
    /// </summary>
    /// <param name="verify">Decides about the server's key; only <see cref="HostKeyMatch.Trusted"/> connects.</param>
    internal async Task<bool> ConnectAsync(ConnectionInfo info, Func<SshHostKey, HostKeyMatch> verify, CancellationToken ct = default)
    {
        TearDown();
        lock (_gate)
        {
            if (_disposed) return false;
            _closing = false;
        }
        RejectedHostKey = null;
        RejectedMatch = null;
        Error = null;
        SetState(SshSessionState.Connecting);
        info.Timeout = ConnectTimeout;
        var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
        client.HostKeyReceived += (_, e) =>
        {
            var key = new SshHostKey(info.Host, info.Port, SshKnownHosts.KeyType(e.HostKeyName), SshKnownHosts.NormalizeFingerprint(e.FingerPrintSHA256));
            var match = verify(key);
            e.CanTrust = match == HostKeyMatch.Trusted;
            if (e.CanTrust) return;
            RejectedHostKey = key;
            RejectedMatch = match;
        };
        client.ErrorOccurred += (_, e) => Fail(SshErrors.Describe(e.Exception, Host));
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var shell = client.CreateShellStream("xterm-256color", _columns, _rows, 0, 0, 64 * 1024);
            var outbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                if (_disposed || _closing)
                {
                    shell.Dispose();
                    client.Dispose();
                    return false;
                }
                _client = client;
                _shell = shell;
                _outbox = outbox;
            }
            ServerVersion = client.ConnectionInfo.ServerVersion;
            ConnectedAt = DateTimeOffset.Now;
            SetState(SshSessionState.Connected);
            _ = Task.Run(() => SendLoopAsync(shell, outbox.Reader));
            new Thread(() => ReadLoop(shell)) { IsBackground = true, Name = "Helm SSH reader" }.Start();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            client.Dispose();
            Error = RejectedHostKey is not null ? null : SshErrors.Describe(ex, Host);
            SetState(SshSessionState.Failed);
            return false;
        }
    }

    /// <summary>Types text into the shell (keys, a paste). Ignored when not connected.</summary>
    public void Send(string text)
    {
        if (text.Length > 0) Post(Encoding.UTF8.GetBytes(text));
    }

    public void Send(byte[] bytes)
    {
        if (bytes.Length > 0) Post(bytes);
    }

    /// <summary>Tells the server the terminal's new size in characters.</summary>
    public void Resize(int columns, int rows)
    {
        var c = (uint)Math.Clamp(columns, 2, 1000);
        var r = (uint)Math.Clamp(rows, 2, 1000);
        lock (_gate)
        {
            if (c == _columns && r == _rows) return;
            _columns = c;
            _rows = r;
        }
        Post(new WindowSize(c, r));
    }

    /// <summary>
    /// Runs a command on its own channel, next to the shell (the shell is not disturbed), and returns its exit code and
    /// output. Used for one-off jobs such as installing this device's key.
    /// </summary>
    public Task<(int ExitCode, string Output)> RunCommandAsync(string command, TimeSpan timeout)
    {
        SshClient? client;
        lock (_gate) client = _client;
        if (client is null || !IsConnected) return Task.FromResult((-1, "Not connected."));
        return Task.Run(() =>
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            cmd.Execute();
            return (cmd.ExitStatus ?? -1, (cmd.Result + cmd.Error).Trim());
        });
    }

    /// <summary>
    /// Runs a command on its own channel and hands its standard output to <paramref name="output"/> as it arrives
    /// (UTF-8, on a background thread). Cancelling stops the command (the server gets TERM, then the channel closes).
    /// </summary>
    public async Task<MenuRunResult> RunStreamingAsync(string command, Action<string>? output, CancellationToken ct)
    {
        SshClient? client;
        lock (_gate) client = _client;
        if (client is null || !IsConnected) return new MenuRunResult(null, "Not connected.");
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = Timeout.InfiniteTimeSpan;
        var run = cmd.BeginExecute();
        await using var stop = ct.Register(() =>
        {
            try
            {
                cmd.CancelAsync(forceKill: false, millisecondsTimeout: 2000);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SshException)
            {
                // Already over.
            }
        }).ConfigureAwait(false);
        await Task.Run(() =>
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            try
            {
                int read;
                while ((read = cmd.OutputStream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                    if (count > 0) output?.Invoke(new string(chars, 0, count));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SshException or InvalidOperationException)
            {
                // The channel closed (cancelled or the connection went away).
            }
        }, CancellationToken.None).ConfigureAwait(false);
        try
        {
            cmd.EndExecute(run);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return new MenuRunResult(null, ct.IsCancellationRequested ? "Stopped." : SshErrors.Describe(ex, Host));
        }
        string error;
        try
        {
            error = cmd.Error;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            error = "";
        }
        return new MenuRunResult(ct.IsCancellationRequested ? null : cmd.ExitStatus, ct.IsCancellationRequested ? "Stopped." : error.Trim());
    }

    /// <summary>Ends the session from Helm's side.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closing && State is SshSessionState.Closed or SshSessionState.Failed) return;
            _closing = true;
        }
        TearDown();
        if (State is SshSessionState.Connected or SshSessionState.Connecting) SetState(SshSessionState.Closed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sink = null;
        }
        Close();
    }

    private void Post(object item)
    {
        Channel<object>? outbox;
        lock (_gate) outbox = _outbox;
        outbox?.Writer.TryWrite(item);
    }

    private async Task SendLoopAsync(ShellStream shell, ChannelReader<object> reader)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (item)
                {
                    case byte[] bytes:
                        shell.Write(bytes, 0, bytes.Length);
                        // Several keys queued together go out in one packet.
                        if (!reader.TryPeek(out var next) || next is not byte[]) shell.Flush();
                        break;
                    case WindowSize size:
                        shell.ChangeWindowSize(size.Columns, size.Rows, 0, 0);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or SocketException or InvalidOperationException)
        {
            // The connection went away; the reader reports it.
        }
    }

    private void ReadLoop(ShellStream shell)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = shell.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                Deliver(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or SocketException or InvalidOperationException)
        {
            // Closed while reading.
        }
        bool closing;
        lock (_gate) closing = _closing;
        if (State == SshSessionState.Connected)
        {
            Deliver("\r\n\u001b[2m[Connection closed]\u001b[0m\r\n"u8);
            if (!closing) TearDown();
            SetState(SshSessionState.Closed);
        }
    }

    private void Deliver(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _buffer.Append(bytes);
            _sink?.Invoke(bytes.ToArray());
        }
    }

    private void Fail(string error)
    {
        if (State is not (SshSessionState.Connected or SshSessionState.Connecting)) return;
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
        }
        Error = error;
        if (State == SshSessionState.Connected) Deliver(Encoding.UTF8.GetBytes($"\r\n\u001b[31m[{error}]\u001b[0m\r\n"));
        TearDown();
        SetState(SshSessionState.Failed);
    }

    private void TearDown()
    {
        SshClient? client;
        ShellStream? shell;
        Channel<object>? outbox;
        lock (_gate)
        {
            client = _client;
            shell = _shell;
            outbox = _outbox;
            _client = null;
            _shell = null;
            _outbox = null;
        }
        outbox?.Writer.TryComplete();
        try
        {
            shell?.Dispose();
            client?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or SocketException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    private void SetState(SshSessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record WindowSize(uint Columns, uint Rows);
}

/// <summary>SSH failures in words a person can act on.</summary>
public static class SshErrors
{
    public static string Describe(Exception ex, SshHost host) => ex switch
    {
        SshAuthenticationException when host.Auth == SshAuthKind.DeviceKey =>
            "The server did not accept this device's key. Add it to ~/.ssh/authorized_keys on the server, or sign in with a password once and choose Install this device's key.",
        SshAuthenticationException when host.Auth == SshAuthKind.KeyFile => $"The server did not accept the key {Path.GetFileName(host.KeyFile)} for {host.User}.",
        SshAuthenticationException when host.Auth == SshAuthKind.Vault => $"The server did not accept the {host.VaultField} of {host.VaultItemTitle} in Vault for {host.User}.",
        SshAuthenticationException => "The server did not accept the user name or password.",
        SocketException s when s.SocketErrorCode == SocketError.HostNotFound => $"No server called {host.Address} was found.",
        SocketException s when s.SocketErrorCode == SocketError.ConnectionRefused => $"{host.Address} refused the connection on port {host.Port}. Is SSH running there?",
        SocketException s => $"Could not reach {host.Address}:{host.Port} ({s.Message}).",
        SshOperationTimeoutException or TimeoutException or OperationCanceledException => $"{host.Address} did not answer in time.",
        SshConnectionException c when c.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.ConnectionLost => "The connection was lost.",
        SshConnectionException c => $"The connection ended: {c.Message}",
        ProxyException p => p.Message,
        _ => ex.Message,
    };
}
