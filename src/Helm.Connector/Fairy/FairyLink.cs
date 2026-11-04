using Helm.Ai.Assistant;
using Helm.Connector.Hosting;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Fairy;

/// <summary>
/// Makes this box a worker of the phone's agent jobs while Fairy Core is here: an <see cref="AgentJobWorker"/> on the
/// <see cref="FairyRoute"/> is started when Core first asks for a job, beats with the agents Core reported, and is
/// stopped (its record removed, so the phone stops listing it) once Core has not asked for <see cref="FairyRoute.SeenFor"/>.
/// </summary>
internal sealed class FairyLink(FairyRoute route, Func<AgentJobWorker> newWorker, ILogger logger, TimeSpan? checkEvery = null) : IServePart
{
    private readonly object _gate = new();
    private AgentJobWorker? _worker;
    private Timer? _check;

    /// <summary>The phone can send jobs here now (the worker record is up).</summary>
    public bool Working
    {
        get { lock (_gate) return _worker is not null; }
    }

    public void Start()
    {
        route.Changed += Changed;
        var every = checkEvery ?? TimeSpan.FromSeconds(10);
        // The worker beats every minute by itself: the check only starts and stops it.
        _check = new Timer(_ => Update(beat: false), null, every, every);
    }

    public void Dispose()
    {
        route.Changed -= Changed;
        _check?.Dispose();
        Stop();
    }

    private void Changed() => Update(beat: true);

    /// <summary>Starts or stops the worker as Core comes and goes; with <paramref name="beat"/>, tells the phone of other agents at once.</summary>
    internal void Update(bool beat)
    {
        AgentJobWorker? started = null, current = null;
        lock (_gate)
        {
            if (!route.Connected)
            {
                Stop();
                return;
            }
            if (_worker is null) started = _worker = newWorker();
            else if (beat) current = _worker;
        }
        if (started is not null)
        {
            started.Start();
            logger.LogInformation("Fairy Core is here: this box takes the phone's agent jobs ({Agents})", string.Join(", ", route.Ready()));
        }
        current?.Beat();
    }

    private void Stop()
    {
        AgentJobWorker? stopped;
        lock (_gate)
        {
            stopped = _worker;
            _worker = null;
        }
        if (stopped is null) return;
        stopped.Dispose();
        logger.LogInformation("Fairy Core is gone: this box no longer takes the phone's agent jobs");
    }
}
