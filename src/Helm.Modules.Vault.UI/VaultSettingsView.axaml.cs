using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Ui;

/// <summary>The vault's settings, as a sheet over the vault page. Done and the X close it.</summary>
public partial class VaultSettingsView : UserControl
{
    private readonly Action? _close;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public VaultSettingsView() => AvaloniaXamlLoader.Load(this);

    public VaultSettingsView(VaultSettingsViewModel viewModel, IVaultPageHost host, Action close)
    {
        DataContext = viewModel;
        _close = close;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => _close?.Invoke();
        this.FindControl<Button>("DoneButton")!.Click += (_, _) => _close?.Invoke();
        // The computer's lock is a desktop thing; a phone locks when Helm goes to the background.
        this.FindControl<Control>("SystemLockRow")!.IsVisible = host.CanLockWithSystem;
        this.FindControl<Control>("BackgroundLockRow")!.IsVisible = !host.CanLockWithSystem;
        this.FindControl<ContentControl>("PlatformCard")!.Content = host.CreatePlatformSettings();
        // Leaving the sheet forgets the passwords typed there.
        DetachedFromVisualTree += (_, _) => viewModel.ForgetTypedSecrets();
    }
}
