using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.Habits;

/// <summary>
/// One habit on the Habits tab: its title and this year's grid, the card coloured by how often it was done in the
/// last 30 days (red to green). Tapping the card opens the pop-up for today (a habit to quit: for yesterday); tapping a
/// cell opens it for that day.
/// </summary>
public sealed partial class HabitCard(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _title = "";

    /// <summary>A habit to quit: the card shows its run of clean days and a way to own up to a slip.</summary>
    [ObservableProperty] private bool _isQuit;

    /// <summary>
    /// The line under the title: for a habit to quit "12 clean days in a row", or when it can first be marked; for a
    /// habit to build, today's share when only part of it was done. Empty for none.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// A habit to build with a streak, not done yet today: "5 days in a row: do it today to keep the chain". Shown as a
    /// warning pill so the habit stands out. Empty for none.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAtRisk))] private string _atRisk = "";

    public bool IsAtRisk => AtRisk.Length > 0;

    /// <summary>A habit to quit whose yesterday is not answered yet: it can be marked clean, or as a slip.</summary>
    [ObservableProperty] private bool _canSlip;

    [ObservableProperty] private HabitYearGrid? _grid;

    /// <summary>The card's border: <see cref="HabitColors.ForRate"/> as "#AARRGGBB".</summary>
    [ObservableProperty] private string _color = HabitColors.Hex(HabitColors.Neutral);

    /// <summary>The card's background: the same colour, faint.</summary>
    [ObservableProperty] private string _tint = HabitColors.Hex(HabitColors.WithAlpha(HabitColors.Neutral, 0x18));

    [ObservableProperty] private bool _isDone;

    /// <summary>For screen readers and tooltips: "Read 20 pages, 12 days in a row, 86 % in the last 30 days".</summary>
    [ObservableProperty] private string _summary = "";
}

/// <summary>A tap on one day of a grid.</summary>
public sealed record HabitDayTap(string HabitId, DateOnly Day);

/// <summary>
/// The pop-up that marks one day of a habit: how much of it was done (a habit to build, <see cref="HabitLimits.MinPercent"/>
/// to 100 %), or whether it was clean (a habit to quit). A day marked already can be unmarked from here.
/// </summary>
public sealed partial class HabitMark(string habitId, string title, DateOnly day, string dayText, string question, bool isQuit, bool wasDone, bool canSlip)
    : ObservableObject
{
    public string HabitId { get; } = habitId;

    public string Title { get; } = title;

    public DateOnly Day { get; } = day;

    /// <summary>"Today", "Yesterday, 2 Nov" or "Mon 26 Oct".</summary>
    public string DayText { get; } = dayText;

    public string Question { get; } = question;

    public bool IsQuit { get; } = isQuit;

    /// <summary>The share is asked for (a habit to build).</summary>
    public bool AsksPercent => !IsQuit;

    /// <summary>The day is marked already: the pop-up shows its share and can unmark it.</summary>
    public bool WasDone { get; } = wasDone;

    /// <summary>A habit to quit, on its yesterday not answered yet: "I slipped" is offered too.</summary>
    public bool CanSlip { get; } = canSlip;

    public string SaveText => IsQuit ? "Yes, I stayed clean" : WasDone ? "Save" : "Done";

    public string UnmarkText => IsQuit ? "Not clean after all" : "Not done after all";

    /// <summary>How much of it was done, in steps of <see cref="HabitLimits.MinPercent"/> (a double, for the sliders).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PercentText))] private double _percent = 100;

    public int Value => (int)Math.Clamp(Math.Round(Percent), HabitLimits.MinPercent, 100);

    public string PercentText => $"{Value}%";
}

/// <summary>A habit in a picker (the report).</summary>
public sealed record HabitChoice(string Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>A habit on the settings page: rename it in place, move it, delete it.</summary>
public sealed partial class HabitRow : ObservableObject
{
    private readonly Action<HabitRow, string> _renamed;
    private bool _fromStore;

    public HabitRow(string id, Action<HabitRow, string> renamed)
    {
        Id = id;
        _renamed = renamed;
    }

    public string Id { get; }

    [ObservableProperty] private string _title = "";

    [ObservableProperty] private string _detail = "";

    [ObservableProperty] private bool _canMoveUp;

    [ObservableProperty] private bool _canMoveDown;

    /// <summary>Sets the title from the store without treating it as an edit.</summary>
    internal void Load(string title)
    {
        _fromStore = true;
        Title = title;
        _fromStore = false;
    }

    partial void OnTitleChanged(string value)
    {
        if (!_fromStore) _renamed(this, value);
    }
}

/// <summary>One month of the report: the share of tracked days done.</summary>
public sealed record HabitMonthBar(string Name, double Rate, string Text, bool HasDays)
{
    /// <summary>Bar height in a 60-unit box (at least a sliver when the month has days).</summary>
    public double Height => HasDays ? Math.Max(2, Rate * 60) : 0;
}

/// <summary>A run of missed days in the report, with its reason when one was written.</summary>
public sealed record HabitLapseRow(string Dates, string Reason, bool HasReason);

/// <summary>What the celebration card shows after a habit is done for the day.</summary>
public sealed record HabitCelebration(string Title, string Message, IReadOnlyList<string> Stats, bool Big);

/// <summary>
/// A run of missed days the user must explain before going on, or a slip the user owned up to (<see cref="IsSlip"/>),
/// which they can still take back. <see cref="DoneText"/> is the button for days that were done after all.
/// </summary>
public sealed record HabitPendingLapse(HabitGap Gap, string HabitTitle, string Heading, string Dates, string Question, string Placeholder, string DoneText,
    bool IsSlip = false)
{
    public bool CanMarkDone => !IsSlip;
}
