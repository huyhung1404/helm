using System.Windows.Media;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Core.Modules;

public static class ModuleGroupIcons
{
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

/// <summary>A Helm tool in the Windows app: <see cref="IModule"/> plus what the WPF shell shows.</summary>
public interface IHelmModule : IModule
{
    SymbolRegular Icon { get; }

    /// <summary>
    /// Optional picture (e.g. a product logo) shown instead of <see cref="Icon"/> wherever the module appears.
    /// Null for most modules. Must be frozen: it is shared by several windows.
    /// </summary>
    ImageSource? IconImage => null;

    /// <summary>A WPF Page resolved from DI for the module's settings tab.</summary>
    Type SettingsPageType { get; }

    /// <summary>Hotkeys owned by the module (shown on Home and checked for conflicts).</summary>
    IReadOnlyList<HotkeyDefinition> Hotkeys { get; }
}

/// <summary>The Windows app's modules.</summary>
public interface IModuleHost : IModuleHost<IHelmModule>;

public sealed class ModuleRegistry(IEnumerable<IHelmModule> modules, ISettingsStoreFactory settings, ILogger<ModuleRegistry> logger)
    : ModuleRegistry<IHelmModule>(modules, settings, logger), IModuleHost;
