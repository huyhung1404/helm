using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Modules;

public static class ModuleGroupIcons
{
    public static Symbol Icon(this ModuleGroup group) => group switch
    {
        ModuleGroup.Planning => Symbol.Calendar,
        ModuleGroup.MoneyAndMedia => Symbol.Money,
        ModuleGroup.SecurityAndServers => Symbol.ShieldLock,
        ModuleGroup.WindowsAndDesktop => Symbol.WindowMultiple,
        ModuleGroup.Learning => Symbol.HatGraduation,
        ModuleGroup.Messages => Symbol.Chat,
        _ => Symbol.Apps,
    };
}

/// <summary>A Helm tool in the Windows app: <see cref="IModule"/> plus what the shell shows (all Avalonia).</summary>
public interface IHelmModule : IModule
{
    /// <summary>The tool's glyph, used where there is no <see cref="IconImage"/>.</summary>
    Symbol Icon { get; }

    /// <summary>The tool's own picture icon (its .UI project's XIcon.Image); null for a glyph only.</summary>
    IImage? IconImage => null;

    /// <summary>
    /// The tool's page: its shared view from Helm.Modules.&lt;X&gt;.UI, with the settings as a sheet inside it (the gear).
    /// The shell calls it once per run, the first time the tool's tab opens, on the UI thread, and keeps the control while
    /// the tab lives (it may move between windows).
    /// </summary>
    Control CreatePage();

    /// <summary>Hotkeys owned by the module (shown on Home and checked for conflicts).</summary>
    IReadOnlyList<HotkeyDefinition> Hotkeys { get; }
}

/// <summary>The Windows app's modules.</summary>
public interface IModuleHost : IModuleHost<IHelmModule>;

public sealed class ModuleRegistry(IEnumerable<IHelmModule> modules, ISettingsStoreFactory settings, ILogger<ModuleRegistry> logger)
    : ModuleRegistry<IHelmModule>(modules, settings, logger), IModuleHost;
