using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>A Claude Code process Helm started (a fake in tests).</summary>
public interface IClaudeProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Task Exited { get; }
    void Kill();
}

/// <summary>Starts <c>claude</c> with these arguments in that folder; null when Claude Code is not installed.</summary>
public interface IClaudeStarter
{
    IClaudeProcess? Start(string folder, IReadOnlyList<string> args);
}

/// <summary>The real one: the Claude Code VS Code runs (else the one on PATH), hidden, never elevated.</summary>
public sealed class ClaudeStarter(IChildProcessLauncher launcher) : IClaudeStarter
{
    public IClaudeProcess? Start(string folder, IReadOnlyList<string> args)
    {
        if ((ClaudeCatalog.ExtensionClaude() ?? McpClientConfig.FindClaude()) is not { } claude) return null;
        var commandLine = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"{CommandLine.Quote(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe")} /d /s /c \"{CommandLine.Join([claude, .. args])}\""
            : CommandLine.Join([claude, .. args]);
        return new Child(launcher.Start(new ChildProcessStartInfo(commandLine, folder)));
    }

    private sealed class Child(ChildProcess process) : IClaudeProcess
    {
        public Stream Input => process.StandardInput;
        public Stream Output => process.StandardOutput;
        public Task Exited => process.Exited;

        public void Kill()
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>What a session Helm runs is set to (the phone's model, effort and mode chips); null keeps Claude's own.</summary>
public sealed record HeadlessOptions(string? Model = null, string? Effort = null, string? PermissionMode = null);

/// <summary>A session Helm runs now.</summary>
public sealed record HostedRun(string SessionId, string Folder, DateTimeOffset StartedAt);

/// <summary>
/// Runs a Claude Code session that is not in a VS Code tab (it ran in a terminal, or another editor) for the phone:
/// <c>claude -p --resume &lt;id&gt;</c> in the session's folder, the same conversation and transcript, with the prompt
/// written to its input (stream-json, so more messages can follow while it works). Claude's hooks report it like any
/// other session; a permission it asks is held for the phone by the hook, and one nobody answered is refused. When its
/// last message is answered, its input is closed and it ends. A terminal that still has the session open does not
/// see these turns: the phone warns before the first one.
/// </summary>
public sealed class HeadlessClaude(IClaudeStarter starter, ILogger logger, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HeadlessOptions> _options = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Said to Claude when a permission it asked was answered by nobody.</summary>
    public const string NobodyAnswered = "Nobody allowed this from the phone in time, so it was not run.";

    /// <summary>A run started or ended. Raised off the UI thread.</summary>
    public event EventHandler? Changed;

    public bool IsRunning(string sessionId) => _runs.ContainsKey(sessionId);

    public IReadOnlyList<HostedRun> Runs => _runs.Values.Select(r => r.Info).OrderBy(r => r.StartedAt).ToList();

    /// <summary>The model, effort or mode of the session's next run.</summary>
    public HeadlessOptions OptionsOf(string sessionId) => _options.GetValueOrDefault(sessionId) ?? new HeadlessOptions();

    public void SetOptions(string sessionId, Func<HeadlessOptions, HeadlessOptions> change) =>
        _options.AddOrUpdate(sessionId, _ => change(new HeadlessOptions()), (_, o) => change(o));

    /// <summary>The arguments of a run (public for the tests).</summary>
    public static IReadOnlyList<string> Arguments(string sessionId, HeadlessOptions options)
    {
        var args = new List<string>
        {
            "-p", "--resume", sessionId, "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            // Permissions come to Helm: Claude's PermissionRequest hook holds them for the phone first.
            "--permission-prompt-tool", "stdio",
        };
        if (options.Model is { Length: > 0 } model) args.AddRange(["--model", model]);
        if (options.Effort is { Length: > 0 } effort && effort != "ultracode" && effort != "ultracode off") args.AddRange(["--effort", effort]);
        if (options.PermissionMode is { Length: > 0 } mode) args.AddRange(["--permission-mode", mode]);
        return args;
    }

    /// <summary>
    /// Hands <paramref name="text"/> to the session: to its run when one is going, else to a new run started in
    /// <paramref name="folder"/>. Returns once the message is written (Claude's UserPromptSubmit hook confirms it).
    /// </summary>
    /// <exception cref="PromptSendException">Claude Code is missing or could not be started.</exception>
    public async Task SendAsync(string sessionId, string folder, string text, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var run = _runs.GetOrAdd(sessionId, id => StartRun(id, folder));
            if (await run.WriteAsync(line, ct).ConfigureAwait(false)) return;
            // That run was just closing: a new one takes the message.
            _runs.TryRemove(new KeyValuePair<string, Run>(sessionId, run));
        }
        throw new PromptSendException("Claude Code on the PC stopped before it took the message.");
    }

    /// <summary>Stops the session's run (the phone's Stop). False when none runs.</summary>
    public bool Stop(string sessionId)
    {
        if (!_runs.TryGetValue(sessionId, out var run)) return false;
        logger.LogInformation("Workspace: stopping the background run of session {Session}", sessionId);
        run.Kill();
        return true;
    }

    private Run StartRun(string sessionId, string folder)
    {
        if (!Directory.Exists(folder)) throw new PromptSendException($"The session's folder is gone from the PC ({folder}).");
        IClaudeProcess? process;
        try
        {
            process = starter.Start(folder, Arguments(sessionId, OptionsOf(sessionId)));
        }
        catch (Exception ex) when (ex is ChildProcessException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new PromptSendException("Claude Code could not be started on the PC: " + ex.Message);
        }
        if (process is null) throw new PromptSendException("Claude Code was not found on the PC (no VS Code extension, nothing on PATH).");
        logger.LogInformation("Workspace: running session {Session} in the background in {Folder}", sessionId, folder);
        var run = new Run(new HostedRun(sessionId, folder, _time.GetLocalNow()), process, logger);
        _ = WatchAsync(run);
        Changed?.Invoke(this, EventArgs.Empty);
        return run;
    }

    private async Task WatchAsync(Run run)
    {
        try
        {
            await run.ReadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Workspace: the background run of {Session} ended with an error", run.Info.SessionId);
        }
        finally
        {
            run.Kill();
            run.Dispose();
            _runs.TryRemove(new KeyValuePair<string, Run>(run.Info.SessionId, run));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var run in _runs.Values) run.Kill();
        _runs.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>One process: messages in, Claude's stream out.</summary>
    private sealed class Run(HostedRun info, IClaudeProcess process, ILogger logger) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly StreamWriter _input = new(process.Input, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        private int _turns;
        private long _lastWrite;
        private bool _closed;

        public HostedRun Info { get; } = info;

        /// <summary>False when the run no longer takes messages (its input was closed after its last answer).</summary>
        public async Task<bool> WriteAsync(string line, CancellationToken ct)
        {
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_closed) return false;
                _turns++;
                _lastWrite = Environment.TickCount64;
                await _input.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _input.FlushAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (IOException)
            {
                _closed = true;
                return false;
            }
            finally
            {
                _write.Release();
            }
        }

        public async Task ReadAsync()
        {
            using var reader = new StreamReader(process.Output, Encoding.UTF8);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                switch (Parse(line))
                {
                    case ("result", _, _, var queued):
                        // Every message answered (Claude may take queued ones in one turn): close the input, and Claude Code ends.
                        await TurnEndedAsync(queued).ConfigureAwait(false);
                        break;
                    case ("control_request", { } id, var subtype, _):
                        await AnswerAsync(id, subtype).ConfigureAwait(false);
                        break;
                }
            }
        }

        /// <summary>A permission the hook did not decide (nobody answered on the phone in time) is refused; anything else is not offered.</summary>
        private async Task AnswerAsync(string id, string? subtype)
        {
            object response = subtype == "can_use_tool"
                ? new { type = "control_response", response = new { subtype = "success", request_id = id, response = new { behavior = "deny", message = NobodyAnswered } } }
                : new { type = "control_response", response = new { subtype = "error", request_id = id, error = "Helm does not offer this." } };
            if (subtype == "can_use_tool") logger.LogInformation("Workspace: a background run asked a permission nobody answered; refused");
            var line = JsonSerializer.Serialize(response);
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed) return;
                await _input.WriteLineAsync(line).ConfigureAwait(false);
                await _input.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            finally
            {
                _write.Release();
            }
        }

        /// <summary>
        /// A turn ended. When nothing waits any more, the input is closed and Claude Code ends. Claude may answer queued
        /// messages in one turn (it says how many still wait); a message written a moment ago may not be counted
        /// there yet, so it keeps the input open.
        /// </summary>
        private async Task TurnEndedAsync(int? queued)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed) return;
                var justWritten = Environment.TickCount64 - _lastWrite < 1500;
                _turns = queued == 0 && !justWritten ? 0 : Math.Max(0, _turns - 1);
                if (_turns > 0) return;
                _closed = true;
                _input.Dispose();
            }
            catch (IOException)
            {
            }
            finally
            {
                _write.Release();
            }
        }

        /// <summary>
        /// The type of a stream-json line; for a control request its id and subtype; for a result how many messages
        /// still wait (null when it does not say).
        /// </summary>
        internal static (string? Type, string? RequestId, string? Subtype, int? Queued) Parse(string line)
        {
            if (line.Length == 0 || line[0] != '{') return (null, null, null, null);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type == "result")
                    return (type, null, null, root.TryGetProperty("queued_turn_count", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n) ? n : null);
                if (type != "control_request") return (type, null, null, null);
                var id = root.TryGetProperty("request_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var subtype = root.TryGetProperty("request", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                return (type, id, subtype, null);
            }
            catch (JsonException)
            {
                return (null, null, null, null);
            }
        }

        public void Kill()
        {
            _closed = true;
            process.Kill();
        }

        public void Dispose()
        {
            try
            {
                _input.Dispose();
            }
            catch (IOException)
            {
            }
            process.Dispose();
        }
    }
}
