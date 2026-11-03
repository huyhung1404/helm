using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Prompts;

/// <summary>
/// Fetches every feature's prompt from the Skill Hub into <see cref="AiPrompts"/>: soon after launch, every
/// <see cref="Every"/>, and when the AI page opens. A failure keeps what was fetched before (until it is too old).
/// </summary>
public sealed class AiPromptRefresher : IDisposable
{
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    /// <summary>The AI page opening asks again only when the last fetch is older than this.</summary>
    internal static readonly TimeSpan Soonest = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(10);

    private readonly AiPrompts _prompts;
    private readonly IAiPromptSource _source;
    private readonly IReadOnlyList<string> _features;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private Timer? _timer;
    private int _running;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private bool _failing;

    /// <param name="features">The features to fetch a prompt for; default: every <see cref="AiFeatureIds"/> constant.</param>
    public AiPromptRefresher(AiPrompts prompts, IAiPromptSource source, IEnumerable<string>? features = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _prompts = prompts;
        _source = source;
        _features = [.. features ?? AllFeatures()];
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Starts the fetches (the PC calls it once at launch).</summary>
    public void Start() => _timer ??= new Timer(_ => _ = RunAsync(), null, FirstLook, Every);

    public void Dispose() => _timer?.Dispose();

    /// <summary>Fetches now in the background, unless the last fetch was within <see cref="Soonest"/>.</summary>
    public void RefreshSoon()
    {
        if (_time.GetUtcNow() - _last >= Soonest) _ = RunAsync();
    }

    /// <summary>
    /// Asks the source for each feature's prompt and keeps the answers. Stops at the first failure: the hub is down or
    /// refuses, and asking for the others would only fail the same way.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            if (!_source.IsSetUp) return;
            _last = _time.GetUtcNow();
            foreach (var feature in _features)
            {
                string? text;
                try
                {
                    text = await _source.GetAsync(AiPrompts.NameOf(feature), ct);
                }
                catch (AiPromptSourceException ex)
                {
                    // Said once when it starts failing, not every quarter of an hour.
                    if (!_failing) _logger.LogWarning("Skill Hub prompts not fetched: {Problem} Helm keeps the ones it has", ex.Message);
                    else _logger.LogDebug("Skill Hub prompts still not fetched: {Problem}", ex.Message);
                    _failing = true;
                    return;
                }
                _prompts.Save(feature, text);
            }
            if (_failing) _logger.LogInformation("Skill Hub prompts fetched again");
            _failing = false;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Every feature id: the string constants of <see cref="AiFeatureIds"/>, so a new feature needs no list.</summary>
    internal static IEnumerable<string> AllFeatures() =>
        typeof(AiFeatureIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private async Task RunAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A timer callback: an exception here would end Helm.
            _logger.LogWarning(ex, "Fetching the Skill Hub prompts failed");
        }
    }
}
