using System.Text.Json.Serialization;

namespace Helm.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum UtilitiesSortMode
{
    Alphabetical,
    Grouped,
}

public sealed class WindowPlacementSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 780;
    public bool IsMaximized { get; set; }
}

/// <summary>general.json: shell-wide preferences plus the enabled state of every module.</summary>
public sealed class GeneralSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;
    public static string StoreId => "general";

    public int Version { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartMinimized { get; set; }
    public UtilitiesSortMode UtilitiesSort { get; set; } = UtilitiesSortMode.Alphabetical;
    public WindowPlacementSettings Window { get; set; } = new();

    /// <summary>moduleId → enabled. Missing ids fall back to the module's default (enabled).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, bool> EnabledModules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastUpdateCheck { get; set; }
}
