namespace Helm.Modules.Vocab;

/// <summary>
/// When the pop-up card comes next, shared by the PC (a timer) and the phone (an alarm): a random time within the chosen
/// range, and a short retry when it could not show (nobody there, a full-screen or blocked app, no word yet).
/// </summary>
public static class VocabPopupSchedule
{
    /// <summary>The longest wait before trying again after a card could not show.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    /// <summary>A random gap between the range's two ends (seconds; swapped or too short ends are put right).</summary>
    public static TimeSpan NextDelay(int minSeconds, int maxSeconds, Random? random = null)
    {
        var min = Math.Max(VocabSettingsViewModel.MinPopupSeconds, Math.Min(minSeconds, maxSeconds));
        var max = Math.Max(min, maxSeconds);
        return TimeSpan.FromSeconds(min + (random ?? Random.Shared).NextDouble() * (max - min));
    }

    /// <summary>The wait before trying again: <see cref="Retry"/>, or the range's longest gap when that is shorter.</summary>
    public static TimeSpan RetryAfter(int maxSeconds)
    {
        var max = TimeSpan.FromSeconds(Math.Max(VocabSettingsViewModel.MinPopupSeconds, maxSeconds));
        return max < Retry ? max : Retry;
    }
}
