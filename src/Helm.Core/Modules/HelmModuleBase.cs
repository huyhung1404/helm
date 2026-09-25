using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Hotkeys;
using Wpf.Ui.Controls;

namespace Helm.Core.Modules;

/// <summary>
/// Convenience base for modules: observable <see cref="IsEnabled"/> / <see cref="StatusMessage"/>.
/// Enabling and disabling is driven by <see cref="ModuleRegistry"/> reacting to <see cref="IsEnabled"/> changes.
/// </summary>
public abstract partial class HelmModuleBase : ObservableObject, IHelmModule
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string? _statusMessage;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract ModuleGroup Group { get; }
    public abstract SymbolRegular Icon { get; }
    public abstract Type SettingsPageType { get; }
    public abstract IReadOnlyList<HotkeyDefinition> Hotkeys { get; }

    public abstract Task EnableAsync(CancellationToken ct);
    public abstract Task DisableAsync();

    /// <summary>Call when <see cref="Hotkeys"/> changed so Home and the conflict checker refresh.</summary>
    protected void NotifyHotkeysChanged() => OnPropertyChanged(nameof(Hotkeys));
}
