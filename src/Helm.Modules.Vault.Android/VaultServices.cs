using CommunityToolkit.Mvvm.Input;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Platform;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Vault;

/// <summary>
/// Both vault pages: the module (the Enable card, is it on), the vault screens and the settings (shared view models),
/// and the way between the content page and the settings page.
/// </summary>
public sealed partial class VaultPageViewModel(VaultModule module, VaultAppViewModel app, VaultSettingsViewModel settings, IShellNavigation navigation)
{
    public VaultModule Module { get; } = module;

    public VaultAppViewModel App { get; } = app;

    public VaultSettingsViewModel Settings { get; } = settings;

    public Avalonia.Media.IImage VaultIcon => Vault.VaultIcon.Image;

    [RelayCommand]
    private void OpenSettings() => navigation.ShowPage(typeof(VaultPage));

    [RelayCommand]
    private void OpenVault() => navigation.ShowPage(typeof(VaultContentPage));
}

public static class VaultServices
{
    public static IServiceCollection AddVaultModule(this IServiceCollection services)
    {
        // Android pieces first: AddVaultCore only fills in what is still missing.
        services.AddSingleton<IVaultDeviceUnlock, BiometricUnlock>();
        services.AddSingleton<IVaultBackupLocation, SafBackupLocation>();
        services.AddSingleton<IVaultPlatform, AndroidVaultPlatform>();
        // A tool that needs a vault secret while the vault is locked (SSH) can open Vault for the password.
        services.AddSingleton<Helm.Core.Secrets.IVaultSecrets>(sp => new Helm.Modules.Vault.Items.VaultSecrets(
            sp.GetRequiredService<VaultSession>(), sp.GetRequiredService<Helm.Modules.Vault.Items.VaultStore>(),
            () => sp.GetRequiredService<IShellNavigation>().ShowPage(typeof(VaultContentPage))));
        services.AddVaultCore();
        services.AddSingleton<VaultPageViewModel>();
        services.AddTransient<VaultContentPage>();
        return services.AddAndroidModule<VaultModule, VaultPage>();
    }
}
