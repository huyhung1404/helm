using System.Text.Json;
using Helm.Ai.Assistant;

namespace Helm.Ai.Auto;

/// <summary>What a job publishes: a brief for the phone, with what it changed (for Undo).</summary>
public sealed record AutoBrief(string Title, string Text, IReadOnlyList<AiJobAction>? Actions = null);

/// <summary>
/// A job a Helm PC runs by itself at its times (<see cref="AutoJobScheduler"/>). It reads the user's data with Helm's
/// own read tools in-process, asks this PC's agent with a short prompt when there is something to say, and returns the
/// brief; null when there is nothing worth a notification.
/// </summary>
public interface IAutoJob
{
    /// <summary>Its <see cref="AiFeatureIds"/> id (on/off, time and route are that feature's choice).</summary>
    string Feature { get; }

    IReadOnlyList<AutoSlot> Slots { get; }

    Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct);
}

/// <summary>Nobody could answer the job's prompt (no agent ready, and the API not allowed): it is tried again later.</summary>
public sealed class AutoJobUnansweredException(string message) : Exception(message);

/// <summary>One run of a job: the slot and period it runs for, the local time, Helm's tools, the agent.</summary>
public sealed class AutoJobContext
{
    private readonly AutoJobTools _tools;
    private readonly Func<string, CancellationToken, Task<string>> _ask;
    private readonly List<string> _remembered;
    private readonly HashSet<string> _known;

    internal AutoJobContext(string slot, string period, DateTime now, AutoJobTools tools, Func<string, CancellationToken, Task<string>> ask,
        IEnumerable<string> remembered)
    {
        Slot = slot;
        Period = period;
        Now = now;
        _tools = tools;
        _ask = ask;
        _remembered = [.. remembered];
        _known = new HashSet<string>(_remembered, StringComparer.Ordinal);
    }

    public string Slot { get; }

    public string Period { get; }

    /// <summary>The user's local time.</summary>
    public DateTime Now { get; }

    public DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>What earlier runs of this job remembered (ids it has seen), oldest first, kept with its runs.</summary>
    public IReadOnlyList<string> Remembered => _remembered;

    public bool Remembers(string id) => _known.Contains(id);

    internal bool RememberedChanged { get; private set; }

    public void Remember(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!_known.Add(id)) continue;
            _remembered.Add(id);
            RememberedChanged = true;
        }
    }

    /// <summary>A read tool's result; null when the tool is off, missing or refused the call.</summary>
    public Task<JsonElement?> ReadAsync(string tool, object? args, CancellationToken ct) => _tools.ReadAsync(tool, args, ct);

    /// <summary>A change Helm can undo; null when it did not go through.</summary>
    public Task<AiJobAction?> ChangeAsync(string tool, object args, string summary, CancellationToken ct) => _tools.ChangeAsync(tool, args, summary, ct);

    /// <exception cref="AutoJobUnansweredException">No agent answered.</exception>
    public Task<string> AskAsync(string prompt, CancellationToken ct) => _ask(prompt, ct);
}
