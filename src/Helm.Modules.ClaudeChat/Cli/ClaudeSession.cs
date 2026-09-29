using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ClaudeChat.Cli;

/// <summary>How the CLI decides on tool calls before asking Helm (<c>--permission-mode</c>).</summary>
public enum ClaudePermissionMode
{
    /// <summary>Ask for anything not already allowed by the user's settings.</summary>
    Manual,

    /// <summary>File edits in the working folder run without asking; everything else still asks.</summary>
    AcceptEdits,

    /// <summary>Read-only planning: nothing is changed.</summary>
    Plan,
}

public sealed record ClaudeSessionOptions(string WorkingDirectory)
{
    public string? Model { get; init; }

    /// <summary>Continue an earlier conversation (the <see cref="SessionStarted.SessionId"/> it reported).</summary>
    public string? ResumeSessionId { get; init; }

    public ClaudePermissionMode PermissionMode { get; init; } = ClaudePermissionMode.Manual;

    /// <summary>An MCP config file (--mcp-config) adding Helm's own tools (notes, Tracker) to the chat.</summary>
    public string? McpConfigPath { get; init; }
}

public sealed class ClaudeSessionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One running <c>claude</c> process speaking stream-json. Events are delivered in order through <see cref="Events"/>
/// (a single reader). Control replies (<see cref="ControlResponse"/>) are consumed here and never surface.
/// </summary>
public sealed class ClaudeSession : IAsyncDisposable
{
    /// <summary>Variables a parent Claude Code session sets for its own children. Passing them on would make the new CLI think it is nested.</summary>
    internal static readonly string[] InheritedSessionVariables =
    [
        "CLAUDECODE", "CLAUDE_AGENT_SDK_VERSION", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING",
        "CLAUDE_CODE_ENABLE_TASKS", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_SESSION_ID", "CLAUDE_EFFORT", "CLAUDE_PID",
        "MCP_CONNECTION_NONBLOCKING",
    ];

    private const int StderrTailLength = 4000;
    private static readonly TimeSpan s_initializeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_gracefulExit = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ChildProcess _process;
    private readonly ILogger _logger;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponse>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _readLoop;
    private readonly Task _stderrLoop;
    private int _nextRequestId;
    private int _disposed;

    private ClaudeSession(ChildProcess process, ILogger logger)
    {
        _process = process;
        _logger = logger;
        _stdin = new StreamWriter(process.StandardInput, s_utf8) { AutoFlush = true, NewLine = "\n" };
        _readLoop = Task.Run(ReadLoopAsync);
        _stderrLoop = Task.Run(StderrLoopAsync);
    }

    /// <summary>Parsed stdout, in order. Completes when the process exits.</summary>
    public ChannelReader<ClaudeEvent> Events => _events.Reader;

    /// <summary>Exit code once the process has ended.</summary>
    public Task<int> Exited => _process.Exited;

    public bool HasExited => _process.HasExited;

    /// <summary>The last few KB the CLI wrote to stderr; shown when it exits unexpectedly.</summary>
    public string StandardErrorTail
    {
        get
        {
            lock (_stderr) return _stderr.ToString().Trim();
        }
    }

    /// <summary>The initialize handshake's payload (slash commands, skills, ...).</summary>
    public JsonElement? Capabilities { get; private set; }

    /// <summary>Starts the CLI and completes the initialize handshake.</summary>
    /// <exception cref="ClaudeSessionException">The process did not start or did not answer.</exception>
    public static async Task<ClaudeSession> StartAsync(IChildProcessLauncher launcher, ClaudeCli cli, ClaudeSessionOptions options, ILogger logger, CancellationToken ct)
    {
        var startInfo = new ChildProcessStartInfo(cli.BuildCommandLine(BuildArguments(options)), options.WorkingDirectory)
        {
            Environment = BuildEnvironment(),
        };

        ChildProcess process;
        try
        {
            process = launcher.Start(startInfo);
        }
        catch (ChildProcessException ex)
        {
            throw new ClaudeSessionException($"Claude Code could not be started: {ex.Message}", ex);
        }

        var session = new ClaudeSession(process, logger);
        logger.LogInformation("Started Claude Code (pid {Pid}) in {Folder}", process.Id, options.WorkingDirectory);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(s_initializeTimeout);
            var reply = await session.SendControlAsync(ClaudeProtocol.Initialize, timeout.Token).ConfigureAwait(false);
            if (!reply.Success) throw new ClaudeSessionException($"Claude Code refused to start the session: {reply.Error}");
            session.Capabilities = reply.Response;
            return session;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ClaudeSessionException or IOException)
        {
            var stderr = session.StandardErrorTail;
            await session.DisposeAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            var detail = string.IsNullOrEmpty(stderr) ? ex.Message : stderr;
            throw new ClaudeSessionException($"Claude Code did not start: {detail}", ex);
        }
    }

    internal static IReadOnlyList<string> BuildArguments(ClaudeSessionOptions options)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio",
        };
        // Asking is the CLI's default; its name changed between versions ("default" → "manual"), so leave it implicit.
        if (options.PermissionMode == ClaudePermissionMode.AcceptEdits) args.AddRange(["--permission-mode", "acceptEdits"]);
        if (options.PermissionMode == ClaudePermissionMode.Plan) args.AddRange(["--permission-mode", "plan"]);
        if (!string.IsNullOrWhiteSpace(options.Model)) args.AddRange(["--model", options.Model]);
        if (!string.IsNullOrWhiteSpace(options.ResumeSessionId)) args.AddRange(["--resume", options.ResumeSessionId]);
        if (!string.IsNullOrWhiteSpace(options.McpConfigPath)) args.AddRange(["--mcp-config", options.McpConfigPath]);
        return args;
    }

    internal static Dictionary<string, string> BuildEnvironment(IDictionary<string, string>? current = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (current is null)
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string?)e.Value ?? string.Empty;
        }
        else
        {
            foreach (var (k, v) in current) env[k] = v;
        }
        foreach (var name in InheritedSessionVariables) env.Remove(name);
        return env;
    }

    public Task SendUserMessageAsync(string text, CancellationToken ct = default) => WriteLineAsync(ClaudeProtocol.UserMessage(text), ct);

    public Task AllowAsync(PermissionRequest request, JsonElement? updatedPermissions = null, CancellationToken ct = default) =>
        WriteLineAsync(ClaudeProtocol.Allow(request, updatedPermissions), ct);

    public Task DenyAsync(PermissionRequest request, string message, CancellationToken ct = default) =>
        WriteLineAsync(ClaudeProtocol.Deny(request, message), ct);

    /// <summary>Uses <paramref name="model"/> (an alias or id) from the next turn on.</summary>
    /// <exception cref="ClaudeSessionException">The CLI refused the model.</exception>
    public async Task SetModelAsync(string model, CancellationToken ct = default)
    {
        var reply = await SendControlAsync(id => ClaudeProtocol.SetModel(id, model), ct).ConfigureAwait(false);
        if (!reply.Success) throw new ClaudeSessionException($"Claude Code did not switch to '{model}': {reply.Error}");
    }

    /// <summary>Stops the running turn. The turn still ends with a <see cref="TurnCompleted"/> event.</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        var reply = await SendControlAsync(ClaudeProtocol.Interrupt, ct).ConfigureAwait(false);
        if (!reply.Success) _logger.LogWarning("Claude Code rejected the interrupt: {Error}", reply.Error);
    }

    private async Task<ControlResponse> SendControlAsync(Func<string, string> build, CancellationToken ct)
    {
        var id = $"helm-{Interlocked.Increment(ref _nextRequestId)}";
        var tcs = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteLineAsync(build(id), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (_process.HasExited) throw new ClaudeSessionException("Claude Code is not running.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new ClaudeSessionException("Claude Code stopped reading its input.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.StandardOutput, s_utf8, detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                foreach (var ev in ClaudeProtocol.Parse(line))
                {
                    if (ev is ControlResponse reply)
                    {
                        if (_pending.TryGetValue(reply.RequestId, out var tcs)) tcs.TrySetResult(reply);
                        continue;
                    }
                    if (ev is UnknownEvent { Type: "(invalid json)" }) _logger.LogDebug("Claude Code wrote a non-JSON line: {Line}", Truncate(line));
                    _events.Writer.TryWrite(ev);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed: the process exited or the session was disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading Claude Code's output failed");
        }
        finally
        {
            var exited = new ClaudeSessionException("Claude Code exited.");
            foreach (var tcs in _pending.Values) tcs.TrySetException(exited);
            _events.Writer.TryComplete();
        }
    }

    private async Task StderrLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.StandardError, s_utf8, detectEncodingFromByteOrderMarks: false);
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > StderrTailLength) _stderr.Remove(0, _stderr.Length - StderrTailLength);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Closes stdin (the CLI exits on EOF), waits briefly, then kills the whole process tree.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _stdin.Dispose();
            }
            finally
            {
                _writeLock.Release();
            }
            await _process.Exited.WaitAsync(s_gracefulExit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            _logger.LogDebug("Claude Code did not exit on its own; killing it");
        }
        _process.Dispose();
        await Task.WhenAll(_readLoop, _stderrLoop).WaitAsync(s_gracefulExit).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
