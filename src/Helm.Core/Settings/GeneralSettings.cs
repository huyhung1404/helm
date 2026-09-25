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
    /// <summary>Null until the window has been placed once (then centered on screen).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 780;
    public bool IsMaximized { get; set; }

    /// <summary>Navigation pane pinned open. Unpinned it collapses to an icon strip and slides out on hover.</summary>
    public bool NavPinned { get; set; }

    /// <summary>Expanded navigation pane width in DIPs (drag its right edge to change).</summary>
    public double NavWidth { get; set; } = 300;
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

    public UpdateSettings Updates { get; set; } = new();

    /// <summary>When true, uninstalling Helm also deletes %LOCALAPPDATA%\Helm (settings and logs).</summary>
    public bool PurgeDataOnUninstall { get; set; }
}

public enum UpdateChannel
{
    Stable,
    Preview,
}

public sealed class UpdateSettings
{
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Download an available update in the background (it is still only installed on request).</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>Apply a downloaded update the next time Helm starts.</summary>
    public bool AutoInstallOnRestart { get; set; }

    /// <summary>Dev only: a local releases folder or file:// URL used instead of GitHub (see docs/testing-updates.md).</summary>
    public string? SourceOverride { get; set; }
}
