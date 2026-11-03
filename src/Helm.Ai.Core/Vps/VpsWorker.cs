using Helm.Ai.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Vps;

/// <summary>How the worker stands, for the Proxy tab.</summary>
/// <param name="Host">The VPS it serves; null while it is not set to.</param>
/// <param name="Problem">Why it is not connected; null while connected or connecting.</param>
public sealed record VpsWorkerStatus(string? Host, bool Connected, int JobsToday, string? Problem);

/// <summary>
/// Serves the user's VPS with this PC's agents: keeps a WebSocket open to it, says which agents are ready, and takes
/// the jobs its services send (a few at once). A job is text only: no Helm tool is offered (the VPS's services have
/// their own data), and each is a conversation of its own, ended once answered. After a drop it connects again,
/// waiting longer each time (1 s up to a minute).
/// </summary>
public sealed class VpsWorker : IDisposable
{
    /// <summary>Jobs run at once; one more is declined, so the VPS can send it elsewhere.</summary>
    public const int MaxJobs = 2;

    /// <summary>How long an agent may take over a job.</summary>
    public static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(2);

    internal const string NeutralSystem = "You are a helpful assistant. Answer the request directly, in the language it is written in.";

    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);

    /// <summary>A connection that stayed up this long starts the waits after a drop again from the shortest.</summary>
    private static readonly TimeSpan StayedUp = TimeSpan.FromSeconds(30);

    private readonly Func<IVpsSocket> _sockets;
    private readonly IAgentRoute _agents;
    private readonly Func<IReadOnlyList<string>> _ready;
    private readonly string _device;
    private readonly string _version;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots = new(MaxJobs, MaxJobs);
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private Link? _link;
    private IReadOnlyList<string> _sent = [];
    private DateOnly _day;
    private int _jobsToday;

    /// <param name="ready">The agents that take jobs now, best first ("Codex", "Grok").</param>
    public VpsWorker(Func<IVpsSocket> sockets, IAgentRoute agents, Func<IReadOnlyList<string>> ready, string device, string version,
        ILogger? logger = null, TimeProvider? time = null)
    {
        _sockets = sockets;
        _agents = agents;
        _ready = ready;
        _device = device;
        _version = version;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The first wait after a drop (tests make it short).</summary>
    internal TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);

    public VpsWorkerStatus Status { get; private set; } = new(null, false, 0, "turned off");

    /// <summary>Raised on a thread-pool thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Serves <paramref name="url"/> from now on (instead of what it served).</summary>
    public void Start(Uri url, string token)
    {
        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _stop?.Cancel();
            _stop = stop;
            _sent = [];
        }
        SetStatus(stop, new VpsWorkerStatus(url.Host, false, JobsToday(), null));
        _loop = Task.Run(() => LoopAsync(url, token, stop));
    }

    /// <summary>Stops serving (closes the connection, cancels its jobs); <paramref name="why"/> is said on the Proxy tab.</summary>
    public void Stop(string why)
    {
        CancellationTokenSource? stop;
        var jobs = JobsToday();
        lock (_gate)
        {
            stop = _stop;
            _stop = null;
            Status = new VpsWorkerStatus(null, false, jobs, why);
        }
        stop?.Cancel();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tells the VPS which agents are ready, if that changed since it last heard.</summary>
    public void SendStatus()
    {
        var agents = _ready();
        Link? link;
        lock (_gate)
        {
            link = _link;
            if (link is null || agents.SequenceEqual(_sent)) return;
            _sent = agents;
        }
        link.Post(VpsProtocol.Status(agents));
    }

    public void Dispose()
    {
        Stop("turned off");
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // It ended with its connection.
        }
    }

    private async Task LoopAsync(Uri url, string token, CancellationTokenSource stop)
    {
        var retry = FirstRetry;
        string? lastProblem = null;
        while (!stop.IsCancellationRequested)
        {
            var started = _time.GetUtcNow();
            var connected = false;
            string problem;
            try
            {
                using var socket = _sockets();
                await socket.ConnectAsync(url, token, stop.Token);
                connected = true;
                started = _time.GetUtcNow();
                await ServeAsync(socket, url, stop);
                problem = "the VPS closed the connection";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (VpsSocketException ex)
            {
                problem = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The connection to the VPS dropped");
                problem = "the connection dropped";
            }
            // Said once, not at every attempt while the VPS stays away.
            if (problem != lastProblem) _logger.LogInformation("Not connected to the VPS at {Host}: {Problem}", url.Host, problem);
            lastProblem = problem;
            SetStatus(stop, new VpsWorkerStatus(url.Host, false, JobsToday(), problem));
            if (connected && _time.GetUtcNow() - started >= StayedUp) retry = FirstRetry;
            try
            {
                await Task.Delay(retry, _time, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
        }
    }

    /// <summary>One connection: hello, then each job as it comes, until it closes. Its jobs are cancelled with it.</summary>
    private async Task ServeAsync(IVpsSocket socket, Uri url, CancellationTokenSource stop)
    {
        using var live = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var link = new Link(socket, live.Token, _logger);
        try
        {
            var agents = _ready();
            await link.SendAsync(VpsProtocol.Hello(_device, agents, _version));
            lock (_gate)
            {
                if (_stop != stop) return;
                _link = link;
                _sent = agents;
            }
            _logger.LogInformation("Serving the VPS at {Host}", url.Host);
            SetStatus(stop, new VpsWorkerStatus(url.Host, true, JobsToday(), null));
            while (await socket.ReceiveAsync(live.Token) is { } frame)
            {
                // Off the receive loop: the readiness check can be slow the first time (finding the agents' CLIs), and a
                // job must never wait behind another's.
                if (VpsProtocol.ReadJob(frame) is { } job) _ = Task.Run(() => Take(link, job, url.Host, stop));
            }
        }
        finally
        {
            lock (_gate)
                if (_link == link) _link = null;
            live.Cancel();
        }
    }

    private void Take(Link link, VpsJob job, string host, CancellationTokenSource stop)
    {
        _logger.LogDebug("VPS job {Id} ({Feature})", job.Id, job.Feature);
        var why = job.Prompt.Length == 0 ? "The job has no prompt."
            : _agents.Unavailable() ?? (_ready().Count == 0 ? $"No agent is ready on {_device}." : null);
        if (why is not null)
        {
            link.Post(VpsProtocol.Decline(job.Id, why));
            return;
        }
        if (!_slots.Wait(0))
        {
            link.Post(VpsProtocol.Decline(job.Id, $"{_device} is running {MaxJobs} jobs already."));
            return;
        }
        _ = Task.Run(() => RunAsync(link, job, host, stop));
    }

    private async Task RunAsync(Link link, VpsJob job, string host, CancellationTokenSource stop)
    {
        try
        {
            await link.SendAsync(VpsProtocol.Claim(job.Id));
            // Text only: no Helm tool, no phone tool; the service's own system prompt.
            var turn = new AiJob(job.Feature, job.Id, job.Prompt, [], [], job.Model, job.Effort) { System = job.System ?? NeutralSystem };
            string answer;
            try
            {
                var result = await _agents.RunAsync(turn, JobTimeout, link.Token);
                answer = VpsProtocol.Result(job.Id, result.Text, result.Agent ?? _agents.Device, result.SavedUsd);
                Count(stop, host);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !link.Token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "VPS job {Id} failed", job.Id);
                answer = VpsProtocol.Error(job.Id, ex is AiRouteException ? ex.Message : $"It failed on {_device}.");
            }
            await link.SendAsync(answer);
        }
        catch (Exception ex) when (ex is OperationCanceledException || link.Token.IsCancellationRequested)
        {
            // The connection went: the VPS no longer waits for this answer.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not answer VPS job {Id}", job.Id);
        }
        finally
        {
            _agents.End(job.Id);
            _slots.Release();
        }
    }

    private void Count(CancellationTokenSource stop, string host)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            if (today != _day) (_day, _jobsToday) = (today, 0);
            _jobsToday++;
        }
        SetStatus(stop, new VpsWorkerStatus(host, true, JobsToday(), null));
    }

    private int JobsToday()
    {
        lock (_gate)
            return DateOnly.FromDateTime(_time.GetLocalNow().DateTime) == _day ? _jobsToday : 0;
    }

    /// <summary>A loop that was stopped or replaced no longer speaks for the worker.</summary>
    private void SetStatus(CancellationTokenSource from, VpsWorkerStatus status)
    {
        lock (_gate)
        {
            if (_stop != from) return;
            Status = status;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A connection's sending side: one frame at a time, as a WebSocket wants.</summary>
    private sealed class Link(IVpsSocket socket, CancellationToken token, ILogger logger)
    {
        private readonly SemaphoreSlim _sending = new(1, 1);

        public CancellationToken Token => token;

        public async Task SendAsync(string frame)
        {
            await _sending.WaitAsync(token);
            try
            {
                await socket.SendAsync(frame, token);
            }
            finally
            {
                _sending.Release();
            }
        }

        /// <summary>Sends without waiting; a frame lost with its connection is lost.</summary>
        public void Post(string frame) => _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(frame);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not send a frame to the VPS");
            }
        });
    }
}
