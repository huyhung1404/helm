namespace Helm.Core.Today;

/// <summary>One thing for today: a task, a habit. Plain data, so a tool's portable core can give it without UI types.</summary>
/// <param name="Id">Stable within the tool; handed back to <see cref="ITodaySource.Tick"/>.</param>
/// <param name="Detail">"Overdue by 1 day", "12-day streak"; null for none.</param>
/// <param name="IsLate">Overdue: Home shows it in red.</param>
/// <param name="CanTick">Can be done right on Home.</param>
/// <param name="IsDone">Already done today (shown ticked).</param>
public sealed record TodayEntry(string Id, string Text, string? Detail = null, bool IsLate = false, bool CanTick = false, bool IsDone = false);

/// <summary>What one tool has for today.</summary>
/// <param name="Headline">"3 open today · 1 overdue", "2 of 4 done".</param>
/// <param name="ActionText">The card's own button ("Open"); null for none.</param>
/// <param name="Urgency">Higher comes higher on Home (overdue first).</param>
public sealed record TodayList(string ModuleId, string Headline, IReadOnlyList<TodayEntry> Entries, string? ActionText, int Urgency);

/// <summary>
/// A tool's part of Home's "today". Each tool registers one in DI; Home lists the sources of the tools that are turned
/// on. Portable: no UI here. Methods are called on the UI thread and must not throw.
/// </summary>
public interface ITodaySource
{
    /// <summary>The tool this belongs to; Home shows it only while that tool is on.</summary>
    string ModuleId { get; }

    /// <summary>Raised (on any thread) when what <see cref="Build"/> would give may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>What the tool has for today; null when nothing.</summary>
    TodayList? Build();

    /// <summary>Does or undoes one entry (a task completed, a habit ticked).</summary>
    void Tick(string entryId, bool done);
}

/// <summary>Something about Helm itself that needs the user (an update ready, sync stuck), as plain data.</summary>
/// <param name="Id">Stable; handed back to <see cref="IHomeAlertSource.Act"/>.</param>
/// <param name="Order">Smaller comes first: the most important first, no levels or colours.</param>
public sealed record HomeAlertData(string Id, string Title, string Detail, string ActionText, int Order);

/// <summary>One kind of alert on Home. Registered in DI; Home lists what every source has.</summary>
public interface IHomeAlertSource
{
    /// <summary>The tool this alert is about; Home lists it only while that tool is on. Null: about Helm itself.</summary>
    string? ModuleId => null;

    event EventHandler? Changed;

    IReadOnlyList<HomeAlertData> Current();

    /// <summary>The alert's button was pressed.</summary>
    Task Act(string alertId);
}
