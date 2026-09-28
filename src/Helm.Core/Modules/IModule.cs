using System.ComponentModel;

namespace Helm.Core.Modules;

public enum ModuleGroup
{
    SystemTools,
    WindowingAndLayouts,
    InputAndOutput,
    FileManagement,
    Advanced,
}

public static class ModuleGroups
{
    public static string DisplayName(this ModuleGroup group) => group switch
    {
        ModuleGroup.SystemTools => "System Tools",
        ModuleGroup.WindowingAndLayouts => "Windowing & Layouts",
        ModuleGroup.InputAndOutput => "Input & Output",
        ModuleGroup.FileManagement => "File Management",
        ModuleGroup.Advanced => "Advanced",
        _ => group.ToString(),
    };
}

/// <summary>
/// The platform-neutral part of a Helm tool. Each app extends it with what its shell needs: <c>IHelmModule</c>
/// (Windows: icon, WPF settings page, hotkeys) and <c>IAndroidModule</c> (Android: icon, Avalonia page).
/// Implementations must be safe to enable/disable repeatedly.
/// </summary>
public interface IModule : INotifyPropertyChanged
{
    /// <summary>Stable id, also the key of the module's settings file and of its enabled state.</summary>
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    ModuleGroup Group { get; }

    /// <summary>Persisted by <see cref="IModuleHost{TModule}"/>; bound to the toggles on Home and on the module page.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Starts hooks/timers/services. Partial failures should be reported through <see cref="StatusMessage"/>.</summary>
    Task EnableAsync(CancellationToken ct);

    /// <summary>Releases everything. Must be idempotent.</summary>
    Task DisableAsync();

    /// <summary>Non-fatal problem shown on the module page (null when all is well).</summary>
    string? StatusMessage { get; }
}
