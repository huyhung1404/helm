using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.Habits;

/// <summary>
/// One habit on the Habits tab: its title and this year's grid, the card coloured by how often it was done in the
/// last 30 days (red to green). Tapping the card ticks today; tapping a cell ticks or unticks that day.
/// </summary>
public sealed partial class HabitCard(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _title = "";

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

/// <summary>A run of missed days the user must explain before going on.</summary>
public sealed record HabitPendingLapse(HabitGap Gap, string HabitTitle, string Heading, string Dates);
