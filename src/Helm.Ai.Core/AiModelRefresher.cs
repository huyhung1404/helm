using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai;

/// <summary>
/// Keeps the models each key offers up to date by itself: a key whose last test is a day old is tested again (soon after
/// launch, then every day), so a model the provider adds shows up in the choice and one it retires leaves it. A feature
/// whose picked model is gone falls back to its provider's default. The tests sync, so one device's test serves all.
/// </summary>
public sealed class AiModelRefresher : IDisposable
{
    /// <summary>A key's model list is tested again once its last test is this old.</summary>
    internal static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>How often the timer looks for a key that is due (a test that found no internet is tried again then).</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>The first look after launch waits this long, so starting Helm stays quick.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(30);

    private readonly AiKeyStore _keys;
    private readonly IReadOnlyDictionary<string, IAiKeyChecker> _checkers;
    private readonly ISyncedSettings<AiFeatureSettings> _features;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private Timer? _timer;
    private int _running;

    public AiModelRefresher(AiKeyStore keys, IEnumerable<IAiKeyChecker> checkers, ISyncedSettings<AiFeatureSettings> features,
        ILogger? logger = null, TimeProvider? time = null)
    {
        _keys = keys;
        _checkers = checkers.ToDictionary(c => c.Provider.Id);
        _features = features;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Starts the daily tests (the PC calls it once at launch).</summary>
    public void Start() => _timer ??= new Timer(_ => _ = RunAsync(), null, FirstLook, Tick);

    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Tests every key whose last test is older than <see cref="Every"/> (or that was never tested), then makes sure each
    /// feature's model is still offered. A test that could not reach the provider keeps the last real answer.
    /// </summary>
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var now = _time.GetUtcNow();
            foreach (var key in _keys.All())
            {
                if (!_checkers.TryGetValue(key.Value.Provider, out var checker)) continue;
                if (key.Value.LastCheck is { } last && now - last.At < Every) continue;
                var check = await checker.CheckAsync(key.Value.Secret, ct);
                if (check.Status == AiKeyStatus.Unknown)
                {
                    _logger.LogDebug("Model list of key {Key}: no answer ({Message}); trying again later", key.Id, check.Message);
                    continue;
                }
                // Deleted meanwhile (here or on another device): nothing to save it to.
                if (_keys.Get(key.Id) is null) continue;
                _keys.SaveCheck(key.Id, check);
                _logger.LogInformation("Model list of key {Key} updated: {Count} chat models", key.Id, AiModels.Chat(check).Count);
            }
            KeepPicksOffered();
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// A feature whose model its provider's key no longer offers moves to the provider's default (or, when that is gone
    /// too, to the first chat model offered). A key without a model list (never tested, rejected) changes nothing.
    /// </summary>
    internal void KeepPicksOffered()
    {
        foreach (var (feature, choice) in _features.Current.Features.ToList())
        {
            if (AiProviders.Find(choice.Provider) is not { } provider || _keys.DefaultFor(provider) is not { } key) continue;
            var offered = AiModels.Chat(key.Value.LastCheck);
            if (offered.Count == 0 || offered.Any(m => m.Id == choice.Model)) continue;
            var fallback = AiModels.DefaultFor(provider);
            var next = offered.Any(m => m.Id == fallback) ? fallback : offered[0].Id;
            _logger.LogInformation("AI feature {Feature}: {Provider} no longer offers {Old}; it uses {New} now", feature, provider.Name, choice.Model, next);
            _features.Update(s =>
            {
                var updated = s.For(feature).Copy();
                updated.Model = next;
                s.Features[feature] = updated;
            });
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshDueAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A timer callback: an exception here would end Helm.
            _logger.LogWarning(ex, "Refreshing the AI model lists failed");
        }
    }
}
