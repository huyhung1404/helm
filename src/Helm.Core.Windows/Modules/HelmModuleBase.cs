using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Hotkeys;

namespace Helm.Core.Modules;

/// <summary>Convenience base for Windows modules; see <see cref="ModuleBase"/>.</summary>
public abstract class HelmModuleBase : ModuleBase, IHelmModule
{
    public abstract Symbol Icon { get; }
    public virtual IImage? IconImage => null;
    public abstract Control CreatePage();
    public abstract IReadOnlyList<HotkeyDefinition> Hotkeys { get; }

    /// <summary>Call when <see cref="Hotkeys"/> changed so Home and the conflict checker refresh.</summary>
    protected void NotifyHotkeysChanged() => OnPropertyChanged(nameof(Hotkeys));
}
