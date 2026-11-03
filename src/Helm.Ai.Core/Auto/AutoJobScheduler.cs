using Helm.Ai.Assistant;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Auto;

/// <summary>
/// Runs the automatic jobs on a Helm PC at their times (the user's local time), each slot once per period whatever
/// restarts (<see cref="AutoJobRuns"/>, synced). With two PCs only one runs them: of the PCs whose agents are ready
/// (their heartbeats in <see cref="AgentJobs.WorkersCollection"/>), the one with the lowest device id. No agent ready:
/// it waits and tries again within the slot's window; the paid API only where the user allowed it. Nothing runs while
/// a Vault is unlocked, and no job ever reads one.
/// </summary>
public sealed class AutoJobScheduler : IDisposable
{
    public static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<IAutoJob> _jobs;
    private readonly ISyncedSettings<AiFeatureSettings> _features;
    private readonly AutoJobRuns _runs;
    private readonly ISyncedCollection<AiWorkerRecord> _workers;
    private readonly AutoJobAsker _asker;
    private readonly AutoJobTools _tools;
    private readonly AiBriefBook _briefs;
    private readonly Func<string> _self;
    private readonly string _device;
    private readonly Func<bool> _vaultInUse;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;

    /// <param name="self">This device's sync id.</param>
    /// <param name="device">This PC's name, written on its briefs.</param>
    public AutoJobScheduler(IEnumerable<IAutoJob> jobs, ISyncedSettings<AiFeatureSettings> features, AutoJobRuns runs,
        ISyncedCollection<AiWorkerRecord> workers, AutoJobAsker asker, AutoJobTools tools, AiBriefBook briefs, Func<string> self, string device,
        Func<bool> vaultInUse, TimeProvider? time = null, TimeZoneInfo? zone = null, ILogger? logger = null)
    {
        _jobs = [.. jobs];
        _features = features;
        _runs = runs;
        _workers = workers;
        _asker = asker;
        _tools = tools;
        _briefs = briefs;
        _self = self;
        _device = device;
        _vaultInUse = vaultInUse;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Start() =>
        _timer ??= _time.CreateTimer(_ => _ = TickSafeAsync(), null, TimeSpan.FromMinutes(1), TickEvery);

    public void Dispose()
    {
        _timer?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Runs every slot that is due now and not done yet; how many ran.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        try
        {
            var ran = 0;
            foreach (var job in _jobs)
            {
                if (AutoJobCatalog.Find(job.Feature) is not { } info) continue;
                var choice = _features.Current.For(job.Feature);
                if (!choice.Enabled) continue;
                var at = AutoJobCatalog.TimeOf(choice, info);
                foreach (var slot in job.Slots)
                {
                    var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime;
                    if (slot.PeriodAt(local, at) is not { } period) continue;
                    // Never next to an unlocked Vault; no agent (and no API allowed): later, within the window.
                    if (_vaultInUse() || !_asker.CanAsk(job.Feature, choice) || !IsRunner(choice)) continue;
                    var key = AutoJobRuns.Key(job.Feature, slot.Name);
                    if (!_runs.TryClaim(key, period, _self(), _time.GetUtcNow())) continue;
                    await RunAsync(job, slot, period, key, choice, local, ct).ConfigureAwait(false);
                    ran++;
                }
            }
            return ran;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// This PC runs the jobs: of the PCs online whose agents are ready (this one counts when its own are), the one with
    /// the lowest device id. Where the API is allowed and no PC has an agent ready, any online PC may, by the same rule.
    /// </summary>
    internal bool IsRunner(AiFeatureChoice choice)
    {
        var me = _self();
        var now = _time.GetUtcNow();
        var online = _workers.All().Where(w => w.Id != me && now - w.Value.SeenAt < AgentJobs.Online).ToList();
        var ready = online.Where(w => w.Value.Problem is null && w.Value.Agents.Count > 0).Select(w => w.Id).ToList();
        if (_asker.AgentReady) ready.Add(me);
        if (ready.Count == 0 && AutoJobAsker.MayUseApi(choice)) ready = [.. online.Select(w => w.Id), me];
        return ready.Count > 0 && ready.Min(StringComparer.Ordinal) == me;
    }

    private async Task RunAsync(IAutoJob job, AutoSlot slot, string period, string key, AiFeatureChoice choice, DateTime local, CancellationToken ct)
    {
        var context = new AutoJobContext(slot.Name, period, local, _tools, (prompt, c) => _asker.AskAsync(job.Feature, choice, prompt, c),
            _runs.Get(key)?.Remembered ?? []);
        string? problem = null;
        try
        {
            if (await job.RunAsync(context, ct).ConfigureAwait(false) is { } brief)
            {
                _briefs.Publish(job.Feature, brief.Title, brief.Text, brief.Actions, _device);
                _briefs.Clean();
            }
            _logger.LogInformation("Auto job {Feature} ({Slot}) ran for {Period}", job.Feature, slot.Name, period);
        }
        catch (AutoJobUnansweredException ex)
        {
            problem = ex.Message;
            _logger.LogInformation("Auto job {Feature} ({Slot}) found no agent to answer; tried again later", job.Feature, slot.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = ex.GetType().Name;
            _logger.LogWarning(ex, "Auto job {Feature} ({Slot}) failed", job.Feature, slot.Name);
        }
        _runs.Finish(key, _time.GetUtcNow(), problem, context.RememberedChanged ? context.Remembered : null);
    }

    private async Task TickSafeAsync()
    {
        try
        {
            await TickAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Timer callback: never let an exception escape.
            _logger.LogWarning(ex, "The automatic jobs could not run");
        }
    }
}
