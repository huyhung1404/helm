using System.ComponentModel;
using Helm.Core.Hotkeys;
using Wpf.Ui.Controls;

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

    public static SymbolRegular Icon(this ModuleGroup group) => group switch
    {
        ModuleGroup.SystemTools => SymbolRegular.Toolbox24,
        ModuleGroup.WindowingAndLayouts => SymbolRegular.WindowMultiple20,
        ModuleGroup.InputAndOutput => SymbolRegular.Keyboard24,
        ModuleGroup.FileManagement => SymbolRegular.Folder24,
        ModuleGroup.Advanced => SymbolRegular.Wrench24,
        _ => SymbolRegular.Apps24,
    };
}

/// <summary>A single Helm tool. Implementations must be safe to enable/disable repeatedly.</summary>
public interface IHelmModule : INotifyPropertyChanged
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    ModuleGroup Group { get; }
    SymbolRegular Icon { get; }

    /// <summary>Persisted by <see cref="IModuleHost"/>; bound to the toggles on Home and on the module page.</summary>
    bool IsEnabled { get; set; }

    /// <summary>A WPF Page resolved from DI for the module's settings tab.</summary>
    Type SettingsPageType { get; }

    /// <summary>Hotkeys owned by the module (shown on Home and checked for conflicts).</summary>
    IReadOnlyList<HotkeyDefinition> Hotkeys { get; }

    /// <summary>Starts hooks/timers. Partial failures should be reported through <see cref="StatusMessage"/>.</summary>
    Task EnableAsync(CancellationToken ct);

    /// <summary>Releases everything. Must be idempotent.</summary>
    Task DisableAsync();

    /// <summary>Non-fatal problem shown as an InfoBar on the module page (null when all is well).</summary>
    string? StatusMessage { get; }
}
