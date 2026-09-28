using FluentIcons.Common;
using Helm.Core.Modules;

namespace Helm.App.Android.Modules;

/// <summary>A Helm tool in the Android app: <see cref="IModule"/> plus what the Avalonia shell shows.</summary>
public interface IAndroidModule : IModule
{
    /// <summary>Fluent System Icon, the same set the Windows app uses (WPF-UI SymbolRegular).</summary>
    Symbol Icon { get; }

    /// <summary>The module's page: an Avalonia control resolved from DI (its DataContext is set by the page itself).</summary>
    Type PageType { get; }
}

/// <summary>Convenience base for Android modules; see <see cref="ModuleBase"/>.</summary>
public abstract class AndroidModuleBase : ModuleBase, IAndroidModule
{
    public abstract Symbol Icon { get; }
    public abstract Type PageType { get; }
}
