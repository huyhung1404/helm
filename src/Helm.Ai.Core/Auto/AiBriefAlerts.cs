using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Ai.Auto;

/// <summary>The briefs this device has told the user of (settings/ai-brief-alerts.json: each device tells once).</summary>
public sealed class AiBriefAlertState : IVersionedSettings
{
    public const string StoreId = "ai-brief-alerts";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Briefs written before this are not told (the first start does not bring up a month of them).</summary>
    public DateTimeOffset? Since { get; set; }

    public List<string> Told { get; set; } = [];
}

/// <summary>
/// Tells the user of each new brief once on this device, as it arrives (a phone notification, a tray balloon on the
/// PC): not one already read, nor one older than <see cref="Fresh"/> (a phone that was off all day does not ring for
/// this morning's brief at night).
/// </summary>
public sealed class AiBriefAlerts(AiBriefBook briefs, ISettingsStore<AiBriefAlertState> state)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(12);
    private const int Remembered = 100;

    private readonly object _gate = new();
    private Action<SyncedItem<AiBrief>>? _show;

    /// <summary>From now on, <paramref name="show"/> gets each new brief once (also those that came while Helm was closed).</summary>
    public void Start(Action<SyncedItem<AiBrief>> show)
    {
        if (_show is not null) return;
        _show = show;
        if (state.Current.Since is null) state.Update(s => s.Since = briefs.Now);
        briefs.Changed += (_, _) => Check();
        Check();
    }

    /// <summary>The briefs to tell of now, marked told.</summary>
    public IReadOnlyList<SyncedItem<AiBrief>> TakeNew()
    {
        lock (_gate)
        {
            var since = state.Current.Since ?? briefs.Now;
            var told = state.Current.Told.ToHashSet(StringComparer.Ordinal);
            var now = briefs.Now;
            var fresh = briefs.Latest(20)
                .Where(b => b.Value.ReadAt is null && b.Value.CreatedAt >= since && now - b.Value.CreatedAt < Fresh && !told.Contains(b.Id))
                .OrderBy(b => b.Value.CreatedAt).ToList();
            if (fresh.Count > 0)
                state.Update(s =>
                {
                    s.Told.AddRange(fresh.Select(b => b.Id));
                    if (s.Told.Count > Remembered) s.Told.RemoveRange(0, s.Told.Count - Remembered);
                });
            return fresh;
        }
    }

    private void Check()
    {
        foreach (var brief in TakeNew()) _show?.Invoke(brief);
    }
}
