using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Core.Modules;

/// <summary>
/// Convenience base for modules on any platform: observable <see cref="IsEnabled"/> / <see cref="StatusMessage"/>.
/// Enabling and disabling is driven by <see cref="ModuleRegistry{TModule}"/> reacting to <see cref="IsEnabled"/> changes.
/// </summary>
public abstract partial class ModuleBase : ObservableObject, IModule
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string? _statusMessage;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract ModuleGroup Group { get; }

    public abstract Task EnableAsync(CancellationToken ct);
    public abstract Task DisableAsync();
}
