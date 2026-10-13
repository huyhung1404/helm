using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>Where Helm Bridge connects: one pipe per Helm data folder (a test copy never talks to the real one).</summary>
public static class WorkspaceEndpoint
{
    /// <summary>Must match <c>pipeName()</c> in Bridge/extension.js.</summary>
    public static string PipeName(HelmPaths paths)
    {
        var root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return "Helm.Workspace." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)), 0, 8);
    }
}

/// <summary>What a VS Code window told Helm about itself.</summary>
public sealed record BridgeWindowInfo
{
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("folders")] public IReadOnlyList<string> Folders { get; init; } = [];
    [JsonPropertyName("sendKey")] public string SendKey { get; init; } = "enter";
    [JsonPropertyName("claudeTabs")] public int ClaudeTabs { get; init; }
    [JsonPropertyName("focused")] public bool Focused { get; init; }

    /// <summary>The Claude tabs by name, in order (Bridge 1.1; null from an older bridge).</summary>
    [JsonPropertyName("tabs")] public IReadOnlyList<BridgeTab>? Tabs { get; init; }

    public bool CtrlEnterSends => SendKey == "ctrl+enter";
}

/// <summary>One Claude tab of a window: Claude names it after the conversation, cut to 25 characters.</summary>
public sealed record BridgeTab
{
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("active")] public bool Active { get; init; }
}

/// <summary>One VS Code window connected through Helm Bridge.</summary>
public sealed class BridgeConnection
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new();

    internal BridgeConnection(Guid id, StreamWriter writer, BridgeWindowInfo info)
    {
        Id = id;
        _writer = writer;
        Info = info;
    }

    public Guid Id { get; }

    public BridgeWindowInfo Info { get; internal set; }

    /// <summary>Asks the window to run one bridge request (open, new, focus, focusEditor, close, ping).</summary>
    /// <param name="label">close: the name of the Claude tab to close.</param>
    /// <exception cref="BridgeException">The window refused, closed or did not answer.</exception>
    public async Task RequestAsync(string op, string? session = null, string? prompt = null, CancellationToken ct = default, string? label = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            var line = JsonSerializer.Serialize(new { type = "request", id, op, session, prompt, label });
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _writer.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new BridgeException("The VS Code window closed.", ex);
            }
            finally
            {
                _write.Release();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            string? error;
            try
            {
                error = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new BridgeException("VS Code did not answer.");
            }
            if (error is not null) throw new BridgeException(error);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal void Answer(string id, bool ok, string? error)
    {
        if (_pending.TryGetValue(id, out var answer)) answer.TrySetResult(ok ? null : error ?? "VS Code refused.");
    }

    internal void Closed()
    {
        foreach (var answer in _pending.Values) answer.TrySetException(new BridgeException("The VS Code window closed."));
    }
}

public sealed class BridgeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Serves Helm Bridge (the VS Code extension) on a named pipe only this Windows user can open, like Helm's MCP pipe
/// (Helm runs elevated, VS Code does not, so the user's SID is granted explicitly and network logons are denied).
/// Each VS Code window keeps one connection; it introduces itself (<c>hello</c>), reports changes (<c>state</c>) and
/// answers requests (<c>reply</c>). Lines of JSON, capped in length.
/// </summary>
public sealed class BridgeHost : IAsyncDisposable
{
    public const int MaxConnections = 32;
    private const int MaxLineLength = 64 * 1024;

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, BridgeConnection> _connections = new();
    private readonly Task _loop;

    public BridgeHost(string pipeName, ILogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
        _loop = AcceptAsync(_stop.Token);
    }

    public string PipeName => _pipeName;

    /// <summary>The windows connected now.</summary>
    public IReadOnlyList<BridgeConnection> Windows => _connections.Values.ToList();

    /// <summary>A window connected, changed or left. Raised off the UI thread.</summary>
    public event EventHandler? WindowsChanged;

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxConnections + 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Workspace bridge pipe {Pipe} could not be created", _pipeName);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _ = ServeAsync(pipe, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        BridgeConnection? connection = null;
        await using (pipe)
        {
            try
            {
                if (_connections.Count >= MaxConnections) return;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" };
                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    if (!TryHandle(line, ref connection, writer)) continue;
                    WindowsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace bridge: a VS Code window's connection failed");
            }
            finally
            {
                if (connection is not null && _connections.TryRemove(connection.Id, out _))
                {
                    connection.Closed();
                    WindowsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    /// <summary>Handles one line; true when the window list changed.</summary>
    private bool TryHandle(string line, ref BridgeConnection? connection, StreamWriter writer)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "hello" or "state":
                BridgeWindowInfo info;
                try
                {
                    info = root.Deserialize<BridgeWindowInfo>() ?? new BridgeWindowInfo();
                }
                catch (JsonException)
                {
                    return false;
                }
                if (connection is null)
                {
                    connection = new BridgeConnection(Guid.NewGuid(), writer, info);
                    _connections[connection.Id] = connection;
                    _logger.LogInformation("Workspace bridge: VS Code window {Name} connected (pid {Pid})", info.Name, info.Pid);
                }
                else
                {
                    if (connection.Info == info) return false;
                    connection.Info = info;
                }
                return true;
            case "reply" when connection is not null:
                var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (id is not null) connection.Answer(id, ok, error);
                return false;
            default:
                return false;
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is not null && line.Length > MaxLineLength) throw new IOException("A bridge message was too long.");
        return line;
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var connection in _connections.Values) connection.Closed();
        _connections.Clear();
        _stop.Dispose();
    }
}
