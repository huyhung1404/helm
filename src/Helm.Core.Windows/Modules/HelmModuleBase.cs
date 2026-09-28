using System.Windows.Media;
using Helm.Core.Hotkeys;
using Wpf.Ui.Controls;

namespace Helm.Core.Modules;

/// <summary>Convenience base for Windows modules; see <see cref="ModuleBase"/>.</summary>
public abstract class HelmModuleBase : ModuleBase, IHelmModule
{
    public abstract SymbolRegular Icon { get; }
    public virtual ImageSource? IconImage => null;
    public abstract Type SettingsPageType { get; }
    public abstract IReadOnlyList<HotkeyDefinition> Hotkeys { get; }

    /// <summary>Call when <see cref="Hotkeys"/> changed so Home and the conflict checker refresh.</summary>
    protected void NotifyHotkeysChanged() => OnPropertyChanged(nameof(Hotkeys));
}
