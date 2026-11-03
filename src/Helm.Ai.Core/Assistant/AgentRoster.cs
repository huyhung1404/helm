namespace Helm.Ai.Assistant;

/// <summary>
/// Follows the user's list of agents on a PC: an agent whose profile is still on keeps what it runs (its conversations,
/// its limits), so renaming, reordering or turning another one on or off never stops a turn in flight. Only agents no
/// longer wanted go, and only new ones are made.
/// </summary>
public static class AgentRoster
{
    /// <param name="before">The agents now.</param>
    /// <param name="wanted">The profiles to run, in the user's order.</param>
    /// <param name="same">Whether an agent runs that profile still (same id and kind).</param>
    /// <param name="keep">The kept agent with the profile's new details (its name).</param>
    /// <param name="make">A new agent for the profile; null when it cannot run here.</param>
    /// <returns>The agents in the new order, and those to dispose.</returns>
    public static (IReadOnlyList<T> Now, IReadOnlyList<T> Gone) Follow<TProfile, T>(IEnumerable<T> before, IEnumerable<TProfile> wanted,
        Func<T, TProfile, bool> same, Func<T, TProfile, T> keep, Func<TProfile, T?> make) where T : class
    {
        var left = before.ToList();
        var now = new List<T>();
        foreach (var profile in wanted)
        {
            var at = left.FindIndex(a => same(a, profile));
            if (at >= 0)
            {
                now.Add(keep(left[at], profile));
                left.RemoveAt(at);
            }
            else if (make(profile) is { } made) now.Add(made);
        }
        return (now, left);
    }
}
