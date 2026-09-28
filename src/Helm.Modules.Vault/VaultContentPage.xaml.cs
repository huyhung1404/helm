using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault;

/// <summary>The settings page: the module (for the Enable card) and the shared settings view model.</summary>
public sealed class VaultPageViewModel(VaultModule module, VaultSettingsViewModel settings)
{
    public VaultModule Module { get; } = module;

    public VaultSettingsViewModel Settings { get; } = settings;
}

/// <summary>The vault page: the module (is it on?), the shared vault screens, and the way to its settings.</summary>
public sealed partial class VaultContentViewModel(VaultModule module, VaultAppViewModel app, IShellNavigation navigation)
{
    public VaultModule Module { get; } = module;

    public VaultAppViewModel App { get; } = app;

    [RelayCommand]
    private void OpenSettings() => navigation.ShowPage(typeof(VaultPage));
}

public partial class VaultContentPage : Page
{
    private readonly VaultContentViewModel _viewModel;
    private readonly IWindowService _windows;
    private readonly ISettingsStore<VaultSettings> _settings;
    private nint _protected;

    public VaultContentPage(VaultContentViewModel viewModel, IWindowService windows, ISettingsStoreFactory settings)
    {
        _viewModel = viewModel;
        _windows = windows;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        DataContext = viewModel;
        InitializeComponent();
        // Any use of the vault counts as activity for the auto-lock.
        PreviewKeyDown += (_, _) => viewModel.App.Session.Touch();
        PreviewMouseDown += (_, _) => viewModel.App.Session.Touch();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Unprotect();
        viewModel.App.PropertyChanged += OnAppChanged;
        _settings.Changed += (_, _) => Dispatcher.BeginInvoke(Protect);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Protect();
        Dispatcher.BeginInvoke(View.FocusFirstInput);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VaultAppViewModel.Screen) || !IsLoaded) return;
        Dispatcher.BeginInvoke(() =>
        {
            Protect();
            View.FocusFirstInput();
        });
    }

    /// <summary>
    /// While the vault is on screen, Helm's window stays out of screenshots, recordings and screen sharing (when the
    /// setting is on); leaving the page gives the window back to capture.
    /// </summary>
    private void Protect()
    {
        if (!IsLoaded || Window.GetWindow(this) is not { } window) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var on = _settings.Current.ProtectFromScreenCapture;
        _windows.SetExcludedFromCapture(hwnd, on);
        _protected = on ? hwnd : 0;
    }

    private void Unprotect()
    {
        if (_protected == 0) return;
        _windows.SetExcludedFromCapture(_protected, false);
        _protected = 0;
    }
}
