using System.Globalization;
using Helm.Core.Services;

namespace Helm.Core.Diagnostics;

/// <summary>
/// Notices when the UI thread stops answering: every <see cref="Interval"/> it posts a no-op to the UI thread and, when
/// that took longer than <see cref="Threshold"/> to run, records "UI froze for 1.8 s" in the black box. Cheap: one
/// timer, one post at a time.
/// </summary>
public sealed class UiStallWatch : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(500);

    /// <summary><see cref="BlackBoxEvent.Source"/> of a freeze: this, then its length in milliseconds.</summary>
    internal const string SourcePrefix = "UI thread ms=";

    private readonly IUiDispatcher _ui;
    private readonly BlackBox _box;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private int _waiting;

    public UiStallWatch(IUiDispatcher ui, BlackBox box, TimeProvider? time = null)
    {
        _ui = ui;
        _box = box;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Tick(), null, Interval, Interval);
    }

    internal void Tick()
    {
        // One probe at a time: while the UI is stuck, the one already posted measures the whole stall.
        if (Interlocked.Exchange(ref _waiting, 1) == 1) return;
        var posted = _time.GetTimestamp();
        try
        {
            _ui.Post(() =>
            {
                var waited = _time.GetElapsedTime(posted);
                Volatile.Write(ref _waiting, 0);
                if (waited >= Threshold) Record(_box, waited);
            });
        }
        catch (Exception)
        {
            Volatile.Write(ref _waiting, 0);
        }
    }

    internal static void Record(BlackBox box, TimeSpan waited) =>
        box.Record(BlackBoxKind.Performance, $"The UI froze for {waited.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s",
            SourcePrefix + ((int)waited.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

    public void Dispose() => _timer.Dispose();
}
