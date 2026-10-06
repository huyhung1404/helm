using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Ssh;

/// <summary>
/// SSH itself, opened from the menu and the Quick access tile (see <see cref="IModuleContent"/>). While the tool is
/// off the servers stay visible but nothing connects, with a note pointing to the settings (Home → Utilities).
/// </summary>
public partial class SshContentPage : Page
{
    private readonly SshModule _module;
    private readonly SshViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public SshContentPage(SshModule module, SshViewModel viewModel, IShellNavigation navigation, IDialogService dialogs, IClipboardService clipboard,
        ISettingsStoreFactory settings, ILogger<SshContentPage> logger)
    {
        _module = module;
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // WebView2 keeps its profile in Helm's cache (the install folder may not be writable). Nothing of the session
        // is stored there: the page's files come from resources and the terminal keeps no history on disk.
        Terminal.Attach(viewModel, dialogs, clipboard, logger, Path.Combine(settings.Paths.Root, "cache", SshIds.ModuleId, "webview2"));
        // Module, view model and page are singletons, so the subscriptions live as long as the page.
        module.PropertyChanged += OnModuleChanged;
        ApplyEnabled();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.BeginInvoke(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        var on = _module.IsEnabled;
        Picker.IsEnabled = on;
        OffBar.IsOpen = !on;
        OffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectAsync();
    }

    /// <summary>View glue: a PasswordBox does not bind. The password goes to this one connection and the box is emptied.</summary>
    private async Task ConnectAsync()
    {
        var password = PasswordBox.Password;
        PasswordBox.Password = "";
        try
        {
            await _viewModel.ConnectAsync(password);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // async void callers: never let an unexpected failure take Helm down.
            _viewModel.Message = "Connecting failed: " + ex.Message;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(SshPage));

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NewHostCommand.Execute(null);
        _navigation.ShowPage(typeof(SshPage));
    }
}
