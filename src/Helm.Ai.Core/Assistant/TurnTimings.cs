using System.Diagnostics;

namespace Helm.Ai.Assistant;

/// <summary>
/// Where one turn's time went, phase by phase ("model 1840 ms · tools 12 ms · model 950 ms"), for the Debug log: the
/// owner reads there whether a slow answer waited on sync, on the agent's start, on the model or on Helm's tools.
/// </summary>
public sealed class TurnTimings
{
    private readonly object _gate = new();
    private readonly List<(string Phase, TimeSpan Took)> _phases = [];
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _last;

    public TurnTimings() => _last = _start;

    /// <summary>The phase that just ended took the time since the last mark (or the start).</summary>
    public void Mark(string phase)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _phases.Add((phase, Stopwatch.GetElapsedTime(_last, now)));
            _last = now;
        }
    }

    public IReadOnlyList<(string Phase, TimeSpan Took)> Phases
    {
        get
        {
            lock (_gate) return [.. _phases];
        }
    }

    public TimeSpan Total => Stopwatch.GetElapsedTime(_start);

    public override string ToString() =>
        string.Join(" · ", Phases.Select(p => $"{p.Phase} {p.Took.TotalMilliseconds:0} ms")) + $" (total {Total.TotalMilliseconds:0} ms)";
}
