using Helm.Core.Sync;

namespace Helm.Ai.Auto;

/// <summary>
/// A job's slot, as it last ran: for which period, on which PC, and how it went. Synced (collection
/// <see cref="AutoJobRuns.Collection"/>, one record per job and slot), so a period runs once whatever restarts, and
/// another PC sees it was done.
/// </summary>
public sealed record AutoJobRun
{
    public required string Period { get; init; }

    /// <summary>The device id of the PC that took it.</summary>
    public required string Device { get; init; }

    public string State { get; init; } = AutoJobRuns.Running;

    /// <summary>How many times this period was tried.</summary>
    public int Attempts { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>Why it failed (never user content).</summary>
    public string? Problem { get; init; }

    /// <summary>Ids the job has dealt with already (the notes it gave titles), kept from period to period.</summary>
    public IReadOnlyList<string> Remembered { get; init; } = [];
}

/// <summary>Claims a slot's period for this PC, so it runs once: done stays done; a failure is tried again a few times.</summary>
public sealed class AutoJobRuns(ISyncedCollection<AutoJobRun> runs)
{
    public const string Collection = "ai.autoruns";

    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>A failed period is tried again after this, within its window.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>A run that says it is running this long after it started stopped with its PC (Helm closed): taken again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    public const int MaxAttempts = 3;

    /// <summary>The most ids a job remembers.</summary>
    public const int MaxRemembered = 300;

    public static string Key(string feature, string slot) => $"{feature}|{slot}";

    public AutoJobRun? Get(string key) => runs.Get(key);

    /// <summary>Takes the period for <paramref name="device"/>, or false when it is done, running elsewhere or out of tries.</summary>
    public bool TryClaim(string key, string period, string device, DateTimeOffset now)
    {
        var last = runs.Get(key);
        var attempts = 1;
        if (last is { } run && run.Period == period)
        {
            if (run.State == Done) return false;
            if (run.State == Running && now - run.At < StaleAfter) return false;
            if (run.State == Failed && now - run.At < RetryAfter) return false;
            if (run.Attempts >= MaxAttempts) return false;
            attempts = run.Attempts + 1;
        }
        runs.Upsert(key, new AutoJobRun
        {
            Period = period, Device = device, State = Running, Attempts = attempts, At = now, Remembered = last?.Remembered ?? [],
        });
        return true;
    }

    public void Finish(string key, DateTimeOffset now, string? problem, IReadOnlyCollection<string>? remembered)
    {
        if (runs.Get(key) is not { } run) return;
        runs.Upsert(key, run with
        {
            State = problem is null ? Done : Failed,
            At = now,
            Problem = problem,
            Remembered = remembered is null ? run.Remembered : [.. remembered.TakeLast(MaxRemembered)],
        });
    }
}
