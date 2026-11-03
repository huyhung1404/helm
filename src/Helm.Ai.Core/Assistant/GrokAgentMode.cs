using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>A running <c>grok agent stdio</c>: its output and input, whether it ended, and how to stop it.</summary>
public sealed class GrokAgentProcess(TextReader output, TextWriter input, Func<bool> hasExited, Action kill)
{
    public TextReader Output => output;
    public TextWriter Input => input;
    public bool HasExited => hasExited();

    /// <summary>Stops it and frees its streams (a read that waits on them ends).</summary>
    public void Kill() => kill();
}

/// <summary>
/// The agent-mode turns of one Grok conversation: one process and session at a time (<see cref="GrokAcp"/>), kept
/// between turns. One that died, was stopped or failed a turn is started again, and its first message has the whole
/// protocol and what was said so far. A session Grok gave tools of its own, did not open in time, or turned down for
/// another reason than the plan's limit or the sign-in (<paramref name="agentFailed"/> says which) is not used:
/// <see cref="TurnAsync"/> returns null, <see cref="Refused"/> says why, and the caller runs the turn another way.
/// </summary>
public sealed class GrokAgentMode(Func<GrokAgentProcess> start, string folder, JsonObject meta, Func<string, bool> agentFailed) : IDisposable
{
    private GrokAgentProcess? _process;
    private GrokAcp? _acp;
    private bool _greeted;
    private readonly List<string> _sessions = [];

    /// <summary>How long a new process may take to open its session (Grok 1.0.70 takes about 0.3 s).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Why agent mode cannot be used, once a start or a session showed it; null until then.</summary>
    public string? Refused { get; private set; }

    /// <summary>Every session this conversation opened (a restart opens another), for the caller to forget.</summary>
    public IReadOnlyList<string> Sessions => _sessions;

    /// <summary>
    /// Starts Grok and opens its session ahead of the first turn (the card opened), so the turn costs neither. Nothing
    /// goes to the model; a session that cannot be used is left for <see cref="TurnAsync"/> to report.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        if (Refused is not null) return;
        using var stop = ct.Register(Kill);
        if (_process is { HasExited: true }) Kill();
        try
        {
            if (_acp is null) await StartAsync(ct);
        }
        catch (Exception ex) when (ex is AssistantException or IOException or ObjectDisposedException)
        {
            // The turn starts it again and says why.
            Kill();
        }
    }

    /// <summary>The turn with what waits in <paramref name="conversation"/>, or null when agent mode cannot be used.</summary>
    /// <exception cref="AssistantException">Grok could not be started, or the turn failed (it says why).</exception>
    public async Task<(string Answer, AssistantUsage Usage)?> TurnAsync(CliConversation conversation, CancellationToken ct)
    {
        if (Refused is not null) return null;
        using var stop = ct.Register(Kill);
        if (_process is { HasExited: true }) Kill();
        try
        {
            if (_acp is null && !await StartAsync(ct)) return null;
            var message = conversation.Message(!_greeted);
            _greeted = true;
            return await _acp!.TurnAsync(message, conversation.Images, ct);
        }
        catch (GrokToolsOfferedException ex)
        {
            // Never seen once a session listed none; the caller sends the turn again another way.
            Kill();
            Refused = ex.Message;
            return null;
        }
        catch (AssistantException ex)
        {
            // A failed turn starts a new session: the next one is sure to get the preamble.
            Kill();
            agentFailed(ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // It died mid-turn (or was stopped): what was sent waits for the next turn, which starts a new process.
            Kill();
            ct.ThrowIfCancellationRequested();
            throw new AssistantException("Grok stopped before it answered.", ex);
        }
    }

    public void Dispose() => Kill();

    private async Task<bool> StartAsync(CancellationToken ct)
    {
        var process = start();
        _process = process;
        var acp = new GrokAcp(process.Output, process.Input);
        using var late = new CancellationTokenSource(StartTimeout);
        using var stopLate = late.Token.Register(Kill);
        try
        {
            await acp.StartAsync(folder, meta, ct);
            if (acp.Session is { } session) _sessions.Add(session);
            _acp = acp;
            _greeted = false;
            return true;
        }
        catch (GrokToolsOfferedException ex)
        {
            Kill();
            Refused = ex.Message;
            return false;
        }
        catch (AssistantException ex) when (!agentFailed(ex.Message))
        {
            Kill();
            Refused = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException && !ct.IsCancellationRequested)
        {
            Kill();
            Refused = late.IsCancellationRequested ? "it did not open a session in time" : ex.Message;
            return false;
        }
    }

    private void Kill()
    {
        _acp = null;
        Interlocked.Exchange(ref _process, null)?.Kill();
    }
}
