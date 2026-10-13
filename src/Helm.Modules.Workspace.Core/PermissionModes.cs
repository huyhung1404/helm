namespace Helm.Modules.Workspace;

/// <summary>
/// Claude Code's permission modes and the order Shift+Tab steps through them in the VS Code panel: Manual (default),
/// Edit automatically (acceptEdits), Plan, then Auto when the account has it. Bypass is never stepped to by Helm.
/// </summary>
public static class PermissionModes
{
    public const string Manual = "default";
    public const string EditAutomatically = "acceptEdits";
    public const string Plan = "plan";
    public const string Auto = "auto";

    public static IReadOnlyList<string> Cycle(bool autoAvailable) =>
        autoAvailable ? [Manual, EditAutomatically, Plan, Auto] : [Manual, EditAutomatically, Plan];

    /// <summary>"manual" is Claude's other name for default.</summary>
    public static string Normalize(string? mode) => mode is null or "" or "manual" ? Manual : mode;

    /// <summary>Shift+Tab presses from one mode to another; -1 when either is not in the cycle (bypass, dontAsk).</summary>
    public static int Presses(string? from, string to, bool autoAvailable)
    {
        var cycle = Cycle(autoAvailable);
        var a = IndexOf(cycle, Normalize(from));
        var b = IndexOf(cycle, Normalize(to));
        return a < 0 || b < 0 ? -1 : (b - a + cycle.Count) % cycle.Count;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    /// <summary>The words VS Code shows for a mode.</summary>
    public static string Label(string? mode) => Normalize(mode) switch
    {
        Manual => "Manual",
        EditAutomatically => "Edit automatically",
        Plan => "Plan",
        Auto => "Auto",
        "bypassPermissions" => "Bypass permissions",
        "dontAsk" => "Don't ask",
        var other => other,
    };

    /// <summary>The effort levels in order (the phone's five dots).</summary>
    public static IReadOnlyList<string> EffortLevels { get; } = ["low", "medium", "high", "xhigh", "max"];

    public static string EffortLabel(string? effort) => effort switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        null or "" => "",
        var other => other,
    };
}
